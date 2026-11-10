using System.Collections.Concurrent;
using System.ComponentModel;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;
using Ortolan.Core.Sync;

namespace EvoLink.Connectors.Sql
{
    /// <summary>
    /// Writes in <see cref="TargetProperty"/> the column <see cref="ResultColumn"/> of the row of <see cref="Table"/> whose column
    /// <see cref="LookupColumn"/> equals the property <see cref="SourceProperty"/> of the entity. Typical use: resolve a foreign key
    /// (the local ID of a product from its Odoo identifier, written by a previous stage).
    /// The results (found or not) are cached per value for the run, the cache being shared by the parallel entities.
    /// Not found: warning and null, or failure of the entity with <see cref="FailWhenNotFound"/>.
    /// </summary>
    [OrtolanType("sql-lookup")]
    [Description("Recherche dans une table SQL la ligne dont une colonne vaut une propriété de l'entité et écrit une autre de ses colonnes dans une propriété (ex. ID local du produit d'après son identifiant Odoo).")]
    public class SqlLookupTransformer : TransformerModule
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _cache = new(StringComparer.Ordinal);
        private ISqlGateway? _gateway;
        private Dictionary<string, Type> _columnTypes = new(StringComparer.OrdinalIgnoreCase);

        [Description("Moteur : SqlServer, MySql, Odbc, OleDb ou Sqlite.")]
        public SqlEngine Engine { get; set; } = SqlEngine.SqlServer;

        [Sensitive]
        [Description("Chaîne de connexion. Peut être une référence '@vault:...'.")]
        public string ConnectionString { get; set; } = string.Empty;

        [Description("Table interrogée (ex. dbo.Produit).")]
        public string Table { get; set; } = string.Empty;

        [Description("Propriété de l'entité contenant la valeur recherchée (ex. product_tmpl_id).")]
        public string SourceProperty { get; set; } = string.Empty;

        [Description("Colonne comparée à la valeur (ex. OdooId).")]
        public string LookupColumn { get; set; } = string.Empty;

        [Description("Colonne dont la valeur est récupérée (ex. ID).")]
        public string ResultColumn { get; set; } = string.Empty;

        [Description("Propriété de l'entité recevant la valeur trouvée (null si introuvable), ex. ProduitId.")]
        public string TargetProperty { get; set; } = string.Empty;

        [Description("Condition SQL supplémentaire (sans WHERE), ex. Actif = 1.")]
        public string? Where { get; set; }

        [Description("Fait échouer l'entité lorsqu'aucune ligne n'est trouvée (sinon avertissement et valeur null).")]
        public bool FailWhenNotFound { get; set; }

        [Description("Délai maximal d'une requête, en secondes.")]
        public int CommandTimeoutSeconds { get; set; } = 120;

        protected virtual ISqlGateway CreateGateway()
            => new DbSqlGateway(Engine, ConnectionString, CommandTimeoutSeconds);

        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            ConnectorGuard.Required(DisplayName,
                (nameof(ConnectionString), ConnectionString), (nameof(Table), Table), ("sourceProperty", SourceProperty),
                ("lookupColumn", LookupColumn), ("resultColumn", ResultColumn), ("targetProperty", TargetProperty));

            _gateway = CreateGateway();

            try
            {
                if (input.Any(e => !e.IsFailed))
                {
                    _columnTypes = await CallAsync(async () =>
                    {
                        await _gateway.OpenAsync(context.CancellationToken);
                        return await _gateway.GetColumnTypesAsync($"SELECT * FROM {_gateway.Dialect.Quote(Table)} WHERE 1 = 0", context.CancellationToken);
                    });

                    foreach (var column in new[] { LookupColumn, ResultColumn })
                    {
                        if (!_columnTypes.ContainsKey(column.Trim()))
                            throw ConnectorGuard.Invalid(DisplayName, $"La colonne '{column}' n'existe pas dans la table '{Table}'.");
                    }
                }

                return await base.ActAsync(input, context);
            }
            finally
            {
                _cache.Clear();
                await _gateway.DisposeAsync();
                _gateway = null;
            }
        }

        public override async Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            var value = ValueNormalizer.Normalize(entity.GetValueOrDefault(SourceProperty));

            if (ValueConverter.IsEmpty(value))
                return NotFound(entity, $"la propriété '{SourceProperty}' est absente ou vide", context);

            var key = SyncComparer.Canonical(value);
            var lookup = _cache.GetOrAdd(key, _ => new Lazy<Task<object?>>(() => SearchAsync(value, context)));
            object? result;

            try
            {
                result = await lookup.Value;
            }
            catch (Exception)
            {
                // A failed search is not cached: the next entity with the same value tries again.
                _cache.TryRemove(key, out _);
                throw;
            }

            if (result is null)
                return NotFound(entity, $"aucune ligne de '{Table}' dont '{LookupColumn}' = '{ValueConverter.ToText(value)}'", context);

            entity[TargetProperty] = result;
            return entity;
        }

        private async Task<object?> SearchAsync(object? value, JobContext context)
        {
            var gateway = _gateway ?? throw new InvalidOperationException("La connexion SQL n'est pas initialisée.");
            var dialect = gateway.Dialect;
            var sql = $"SELECT {dialect.Quote(ResultColumn.Trim())} FROM {dialect.Quote(Table)} WHERE {dialect.Quote(LookupColumn.Trim())} = {dialect.Parameter(0)}";

            if (!string.IsNullOrWhiteSpace(Where))
                sql += $" AND ({Where})";

            var parameter = SqlValues.ToColumnType(value, _columnTypes.GetValueOrDefault(LookupColumn.Trim()));
            var rows = await CallAsync(() => gateway.QueryAsync(sql, [parameter], context.CancellationToken));

            if (rows.Count > 1)
                context.LogWarning($"[{DisplayName}] Plusieurs lignes de '{Table}' ont '{LookupColumn}' = '{ValueConverter.ToText(value)}' : la première est utilisée.");

            return rows.Count > 0 ? SqlValues.FromDatabase(rows[0].Values.FirstOrDefault()) : null;
        }

        private Entity NotFound(Entity entity, string reason, JobContext context)
        {
            if (FailWhenNotFound)
                throw new SqlRowNotFoundException($"Valeur introuvable : {reason}.");

            entity[TargetProperty] = null;
            context.LogWarning($"[{DisplayName}] Valeur introuvable ({reason}) pour {entity.ToFlatString(200)}.");

            return entity;
        }

        private Task<T> CallAsync<T>(Func<Task<T>> call)
            => ConnectorGuard.CallAsync(
                call,
                _ => _gateway?.IsConnectionBroken ?? true,
                ex => ConnectorGuard.SourceUnavailable(DisplayName, Table, ex));
    }

    /// <summary>No row matches the searched value.</summary>
    public sealed class SqlRowNotFoundException : Exception
    {
        public SqlRowNotFoundException(string message) : base(message) { }
    }
}
