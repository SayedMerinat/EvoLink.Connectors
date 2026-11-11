using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using EvoLink.Connectors.Common;
using Newtonsoft.Json;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;
using Ortolan.Core.Sync;

namespace EvoLink.Connectors.Sql
{
    /// <summary>
    /// Reads the rows of a table or of a query (one entity per row).
    /// </summary>
    [OrtolanType("sql-provider")]
    [Description("Lit les lignes d'une table ou d'une requête SQL (SQL Server, MySQL, ODBC, OleDB, SQLite).")]
    public class SqlProviderModule : ProviderModule
    {
        [Description("Moteur : SqlServer, MySql, Odbc, OleDb ou Sqlite.")]
        public SqlEngine Engine { get; set; } = SqlEngine.SqlServer;

        [Sensitive]
        [Description("Chaîne de connexion. Peut être une référence '@vault:...'.")]
        public string ConnectionString { get; set; } = string.Empty;

        [Description("Table lue (ex. dbo.Clients). Ignorée si une requête est fournie.")]
        public string? Table { get; set; }

        [Description("Requête SELECT complète, à la place de la table.")]
        public string? Query { get; set; }

        [Description("Condition SQL ajoutée à la lecture de la table (sans WHERE), ex. Actif = 1.")]
        public string? Where { get; set; }

        [Description("Colonne de date de modification : seules les lignes modifiées depuis la dernière synchronisation réussie sont lues.")]
        public string? IncrementalColumn { get; set; }

        [Description("Correspondances propriété de l'entité → colonne.")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("Ne lit que les colonnes présentes dans les correspondances.")]
        public bool OnlyMappedFields { get; set; }

        [Description("Délai maximal d'une requête, en secondes.")]
        public int CommandTimeoutSeconds { get; set; } = 120;

        protected virtual ISqlGateway CreateGateway()
            => new DbSqlGateway(Engine, ConnectionString, CommandTimeoutSeconds);

        public override async Task<List<Entity>> FetchAsync(JobContext context)
        {
            ConnectorGuard.Required(DisplayName, (nameof(ConnectionString), ConnectionString));
            if (string.IsNullOrWhiteSpace(Table) && string.IsNullOrWhiteSpace(Query))
                throw ConnectorGuard.Invalid(DisplayName, "Une table ou une requête est obligatoire.");

            var mapping = new FieldMapping(Fields, OnlyMappedFields);
            await using var gateway = CreateGateway();

            var (sql, parameters) = BuildQuery(gateway.Dialect, mapping, context);

            var rows = await ConnectorGuard.CallAsync(
                async () =>
                {
                    await gateway.OpenAsync(context.CancellationToken);
                    return await gateway.QueryAsync(sql, parameters, context.CancellationToken);
                },
                _ => gateway.IsConnectionBroken,
                ex => ConnectorGuard.SourceUnavailable(DisplayName, Table ?? "requête SQL", ex));

            return rows.Select(row => mapping.ToEntity(row.Select(c => new KeyValuePair<string, object?>(c.Key, SqlValues.FromDatabase(c.Value))))).ToList();
        }

        internal (string Sql, List<object?> Parameters) BuildQuery(SqlDialect dialect, FieldMapping mapping, JobContext context)
        {
            var parameters = new List<object?>();
            var conditions = new List<string>();
            string sql;

            if (!string.IsNullOrWhiteSpace(Query))
            {
                sql = Query.Trim().TrimEnd(';');

                if (!string.IsNullOrWhiteSpace(IncrementalColumn) && context.LastSuccessfulRun is { } since)
                {
                    sql = $"SELECT * FROM ({sql}) q WHERE {dialect.Quote(IncrementalColumn)} > {dialect.Parameter(0)}";
                    parameters.Add(since.LocalDateTime);
                }

                return (sql, parameters);
            }

            var columns = mapping.OnlyMappedFields ? string.Join(", ", Fields.Values.Select(dialect.Quote)) : "*";
            sql = $"SELECT {columns} FROM {dialect.Quote(Table!)}";

            if (!string.IsNullOrWhiteSpace(Where))
                conditions.Add($"({Where})");

            if (!string.IsNullOrWhiteSpace(IncrementalColumn) && context.LastSuccessfulRun is { } lastRun)
            {
                conditions.Add($"{dialect.Quote(IncrementalColumn)} > {dialect.Parameter(parameters.Count)}");
                parameters.Add(lastRun.LocalDateTime);
            }

            if (conditions.Count > 0)
                sql += " WHERE " + string.Join(" AND ", conditions);

            return (sql, parameters);
        }
    }

    /// <summary>
    /// Synchronises the entities into a table: INSERT, UPDATE of the changed columns, DELETE (or logical deletion).
    /// The values of the entities are converted to the types of the columns before the comparison, so that "06.06.2017"
    /// equals a DATE column or "1" a BIT column.
    /// </summary>
    [OrtolanType("sql-synchroniser")]
    [Description("Synchronise les entités dans une table SQL (SQL Server, MySQL, ODBC, OleDB, SQLite) : insertion, mise à jour des colonnes modifiées, suppression.")]
    public class SqlSynchroniserModule : SynchroniserModule
    {
        /// <summary>Metadata receiving the value of <see cref="IdentityColumn"/> (created, updated or unchanged row).</summary>
        public const string IdMetadataKey = "sql.id";

        /// <summary>Metadata receiving the synchronised table.</summary>
        public const string TableMetadataKey = "sql.table";

        private ISqlGateway? _gateway;
        private Dictionary<string, Type> _columnTypes = new(StringComparer.OrdinalIgnoreCase);
        private FieldMapping _mapping = new(null);

        [Description("Moteur : SqlServer, MySql, Odbc, OleDb ou Sqlite.")]
        public SqlEngine Engine { get; set; } = SqlEngine.SqlServer;

        [Sensitive]
        [Description("Chaîne de connexion. Peut être une référence '@vault:...'.")]
        public string ConnectionString { get; set; } = string.Empty;

        [Description("Table cible (ex. dbo.Clients).")]
        public string Table { get; set; } = string.Empty;

        [Description("Condition SQL (sans WHERE) délimitant les lignes de la cible lues et gérées ; en mode miroir, seules ces lignes peuvent être supprimées.")]
        public string? Where { get; set; }

        [Description("Correspondances propriété de l'entité → colonne.")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("N'écrit que les colonnes présentes dans les correspondances.")]
        public bool OnlyMappedFields { get; set; }

        [Description("Colonne de suppression logique : la suppression la met à SoftDeleteValue au lieu d'effacer la ligne.")]
        public string? SoftDeleteColumn { get; set; }

        [Description("Valeur écrite dans la colonne de suppression logique.")]
        public string SoftDeleteValue { get; set; } = "1";

        [Description("Applique toutes les opérations dans une transaction validée à la fin (sinon chaque opération est validée immédiatement et la reprise se fait ligne par ligne).")]
        public bool UseTransaction { get; set; }

        [Description("Colonne générée par la base (IDENTITY, AUTO_INCREMENT, valeur par défaut...) : jamais écrite, sa valeur est relue après l'insertion et donnée à l'entité dans la métadonnée 'sql.id', que la ligne soit créée, mise à jour ou inchangée.")]
        public string? IdentityColumn { get; set; }

        [Description("Propriété de l'entité recevant aussi la valeur de la colonne générée (ex. ProduitId), pour la référencer dans les modules suivants. Vide = métadonnée 'sql.id' seulement.")]
        public string? IdentityProperty { get; set; }

        [Description("Délai maximal d'une requête, en secondes.")]
        public int CommandTimeoutSeconds { get; set; } = 120;

        [Description("Crée la table si elle n'existe pas : une colonne par propriété des entités, dans l'ordre où elles apparaissent, avec le type de columnTypes ou déduit des valeurs (SQL Server, MySQL, SQLite).")]
        public bool CreateTable { get; set; }

        [Description("Ajoute à la table les colonnes des propriétés qu'elle n'a pas encore (un champ ajouté à la source apparaît dans la table).")]
        public bool AddMissingColumns { get; set; }

        [Description("Type SQL de certaines colonnes créées, ex. { \"date_order\": \"DATE\", \"price\": \"MONEY\" }. Les autres sont déduits des valeurs (texte : NVARCHAR(MAX)).")]
        public Dictionary<string, string> ColumnTypes { get; set; } = [];

        [JsonIgnore]
        protected override bool AppliesImmediately => !UseTransaction;

        private bool HasIdentity => !string.IsNullOrWhiteSpace(IdentityColumn);

        protected virtual ISqlGateway CreateGateway()
            => new DbSqlGateway(Engine, ConnectionString, CommandTimeoutSeconds);

        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, (nameof(ConnectionString), ConnectionString), (nameof(Table), Table));

            _mapping = new FieldMapping(Fields, OnlyMappedFields);
            _gateway = CreateGateway();

            var tableMissing = false;

            try
            {
                await Guard(async () =>
                {
                    await _gateway.OpenAsync(context.CancellationToken);

                    if (await LoadColumnsAsync(input, context) is { } columns)
                        _columnTypes = columns;
                    else
                        tableMissing = true;

                    if (UseTransaction && !tableMissing)
                        await _gateway.BeginTransactionAsync(context.CancellationToken);
                });

                // createTable without any entity: no column to deduce, but nothing to write nor to delete either.
                if (tableMissing)
                {
                    context.LogInfo($"[{DisplayName}] La table '{Table}' n'existe pas encore et aucune entité n'est à écrire : elle sera créée au premier enregistrement.");
                    await CloseAsync(context);
                    return input;
                }

                if (HasIdentity && !_columnTypes.ContainsKey(IdentityColumn!))
                    throw ConnectorGuard.Invalid(DisplayName, $"La colonne générée '{IdentityColumn}' n'existe pas dans la table '{Table}'.");

                if (HasIdentity && KeyProperties(context).Any(IsIdentity))
                    throw ConnectorGuard.Invalid(DisplayName, $"La colonne générée '{IdentityColumn}' ne peut pas être un champ clé : elle n'existe pas encore dans la source. Utilisez une clé métier (ex. l'identifiant de la source).");
            }
            catch
            {
                // The base class closes the connection only once it has started: a refused configuration must not leak it.
                await CloseAsync(context);
                throw;
            }

            foreach (var entity in input)
                ConvertToColumnTypes(entity);

            // Only the properties stored in a column are compared, otherwise a property absent from the table would
            // make every record look modified. The generated column is never compared nor written.
            var compared = (SyncFields ?? context.Sync.SyncFields) is { Count: > 0 } configured
                ? configured
                : input.SelectMany(e => e.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            SyncFields = compared.Where(p => _columnTypes.ContainsKey(_mapping.ToField(p)) && (!_mapping.OnlyMappedFields || Fields.ContainsKey(p)) && !IsIdentity(p)).ToList();

            var output = await base.ActAsync(input, context);

            if (HasIdentity && !string.IsNullOrWhiteSpace(IdentityProperty))
            {
                foreach (var entity in output.Where(e => !e.IsFailed && e.SyncResult != SyncResult.Deleted))
                {
                    if (entity.Metadata.TryGetValue(IdMetadataKey, out var id))
                        entity[IdentityProperty] = long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : id;
                }
            }

            return output;
        }

        protected override async Task<IEnumerable<Entity>> ReadTargetAsync(JobContext context)
        {
            var sql = $"SELECT * FROM {Quote(Table)}" + (string.IsNullOrWhiteSpace(Where) ? string.Empty : $" WHERE ({Where})");
            var rows = await _gateway!.QueryAsync(sql, [], context.CancellationToken);

            return rows.Select(row =>
            {
                var entity = _mapping.ToEntity(row.Select(c => new KeyValuePair<string, object?>(c.Key, SqlValues.FromDatabase(c.Value))));

                // The base class gives these metadata to the source entity when the row is updated or unchanged.
                if (HasIdentity && row.TryGetValue(IdentityColumn!, out var id))
                    SetIdentity(entity, id);

                return entity;
            }).ToList();
        }

        protected override async Task<Entity> CreateAsync(Entity source, JobContext context)
        {
            var values = WritableFields(source, all: true);
            if (values.Count == 0)
                throw new InvalidOperationException("Aucune colonne de la table ne correspond aux propriétés de l'entité.");

            var dialect = _gateway!.Dialect;
            var sql = $"INSERT INTO {Quote(Table)} ({string.Join(", ", values.Select(v => dialect.Quote(v.Column)))}) " +
                      $"VALUES ({string.Join(", ", values.Select((_, i) => dialect.Parameter(i)))})";

            await RecordAsync(() => _gateway.ExecuteAsync(sql, values.Select(v => v.Value).ToList(), context.CancellationToken));

            if (HasIdentity)
                await ReadIdentityAsync(source, context);

            return source;
        }

        /// <summary>
        /// Reads back the generated value of the inserted row by its key. This works with every engine (IDENTITY, AUTO_INCREMENT,
        /// sequences, default values, triggers), in the transaction of the synchroniser when there is one.
        /// </summary>
        private async Task ReadIdentityAsync(Entity source, JobContext context)
        {
            var keys = KeyValues(source, context);
            var dialect = _gateway!.Dialect;
            var sql = $"SELECT {dialect.Quote(IdentityColumn!)} FROM {Quote(Table)} " +
                      $"WHERE {string.Join(" AND ", keys.Select((k, i) => $"{dialect.Quote(k.Column)} = {dialect.Parameter(i)}"))}";

            var rows = await _gateway.QueryAsync(sql, keys.Select(k => k.Value).ToList(), context.CancellationToken);

            if (rows.Count == 1 && rows[0].Values.FirstOrDefault() is { } id)
                SetIdentity(source, id);
            else
                context.LogWarning($"[{DisplayName}] Ligne créée mais sa colonne '{IdentityColumn}' n'a pas pu être relue ({rows.Count} ligne(s) pour la clé) : {source.ToFlatString(200)}");
        }

        private void SetIdentity(Entity entity, object? value)
        {
            if (SqlValues.FromDatabase(value) is not { } id)
                return;

            entity.Metadata[IdMetadataKey] = Convert.ToString(id, CultureInfo.InvariantCulture) ?? string.Empty;
            entity.Metadata[TableMetadataKey] = Table;
        }

        /// <summary>
        /// Columns of the table. With <see cref="CreateTable"/> a missing table is created, with <see cref="AddMissingColumns"/> the
        /// properties without column get one; nothing else of the table is ever changed (no DROP, no type change).
        /// </summary>
        /// <returns>The columns, or null when the table is missing and no entity gives its columns (nothing to synchronise).</returns>
        private async Task<Dictionary<string, Type>?> LoadColumnsAsync(List<Entity> input, JobContext context)
        {
            var probe = $"SELECT * FROM {Quote(Table)} WHERE 1 = 0";

            if (!CreateTable && !AddMissingColumns)
                return await _gateway!.GetColumnTypesAsync(probe, context.CancellationToken);

            if (!SqlSchema.Supports(Engine))
                throw ConnectorGuard.Invalid(DisplayName, $"createTable et addMissingColumns ne sont disponibles qu'avec SqlServer, MySql et Sqlite (moteur : {Engine}).");

            if (ColumnTypes.Values.Select(SqlSchema.CheckType).FirstOrDefault(e => e != null) is { } invalid)
                throw ConnectorGuard.Invalid(DisplayName, invalid);

            var types = new Dictionary<string, string>(ColumnTypes, StringComparer.OrdinalIgnoreCase);
            var entities = input.Where(e => !e.IsFailed && e.SyncAction != SyncAction.Delete);
            var wanted = SqlSchema.Plan(Engine, entities, ColumnOf, types, IdentityColumn);
            Dictionary<string, Type> columns;

            try
            {
                columns = await _gateway!.GetColumnTypesAsync(probe, context.CancellationToken);
            }
            catch (DbException) when (CreateTable && !_gateway!.IsConnectionBroken)
            {
                if (wanted.Count == 0 && !entities.Any())
                    return null;

                if (wanted.Count == 0)
                    throw ConnectorGuard.Invalid(DisplayName, $"La table '{Table}' n'existe pas et aucune propriété des entités ne donne de colonne.");

                await _gateway.ExecuteAsync(SqlSchema.CreateTable(_gateway.Dialect, Table, wanted, IdentityColumn), [], context.CancellationToken);
                context.LogInfo($"[{DisplayName}] Table '{Table}' créée avec {wanted.Count + (HasIdentity ? 1 : 0)} colonne(s).");

                return await _gateway.GetColumnTypesAsync(probe, context.CancellationToken);
            }

            var missing = AddMissingColumns ? wanted.Where(c => !columns.ContainsKey(c.Column)).ToList() : [];
            if (missing.Count == 0)
                return columns;

            foreach (var (column, type) in missing)
                await _gateway.ExecuteAsync(SqlSchema.AddColumn(_gateway.Dialect, Table, column, type), [], context.CancellationToken);

            context.LogInfo($"[{DisplayName}] Colonne(s) ajoutée(s) à la table '{Table}' : {string.Join(", ", missing.Select(c => c.Column))}.");
            return await _gateway.GetColumnTypesAsync(probe, context.CancellationToken);
        }

        /// <summary>Column of a property, null when the mapping leaves it out.</summary>
        private string? ColumnOf(string property)
            => _mapping.OnlyMappedFields && !Fields.ContainsKey(property) ? null : _mapping.ToField(property);

        private bool IsIdentity(string property)
            => HasIdentity && string.Equals(_mapping.ToField(property), IdentityColumn, StringComparison.OrdinalIgnoreCase);

        protected override async Task<Entity> UpdateAsync(Entity source, Entity existing, JobContext context)
        {
            var syncFields = SyncFields ?? context.Sync.SyncFields;
            var changed = SyncComparer.GetChangedFields(source, existing, syncFields).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var values = WritableFields(source, all: false).Where(v => changed.Contains(v.Property) && !KeyProperties(context).Contains(v.Property, StringComparer.OrdinalIgnoreCase)).ToList();

            if (values.Count == 0)
                return source;

            var keys = KeyValues(existing, context);
            var dialect = _gateway!.Dialect;
            var sql = $"UPDATE {Quote(Table)} SET {string.Join(", ", values.Select((v, i) => $"{dialect.Quote(v.Column)} = {dialect.Parameter(i)}"))} " +
                      $"WHERE {string.Join(" AND ", keys.Select((k, i) => $"{dialect.Quote(k.Column)} = {dialect.Parameter(values.Count + i)}"))}";

            await RecordAsync(() => _gateway.ExecuteAsync(sql, [.. values.Select(v => v.Value), .. keys.Select(k => k.Value)], context.CancellationToken));
            return source;
        }

        protected override async Task DeleteAsync(Entity existing, JobContext context)
        {
            var keys = KeyValues(existing, context);
            var dialect = _gateway!.Dialect;
            var where = string.Join(" AND ", keys.Select((k, i) => $"{dialect.Quote(k.Column)} = {dialect.Parameter(i + (string.IsNullOrWhiteSpace(SoftDeleteColumn) ? 0 : 1))}"));

            string sql;
            List<object?> parameters;

            if (string.IsNullOrWhiteSpace(SoftDeleteColumn))
            {
                sql = $"DELETE FROM {Quote(Table)} WHERE {where}";
                parameters = keys.Select(k => k.Value).ToList();
            }
            else
            {
                sql = $"UPDATE {Quote(Table)} SET {dialect.Quote(SoftDeleteColumn)} = {dialect.Parameter(0)} WHERE {where}";
                parameters = [ConvertValue(SoftDeleteValue, _columnTypes.GetValueOrDefault(SoftDeleteColumn)), .. keys.Select(k => k.Value)];
            }

            await RecordAsync(() => _gateway.ExecuteAsync(sql, parameters, context.CancellationToken));
        }

        protected override Task CommitAsync(JobContext context)
            => UseTransaction ? _gateway!.CommitAsync(context.CancellationToken) : Task.CompletedTask;

        protected override async Task CloseAsync(JobContext context)
        {
            if (_gateway != null)
                await _gateway.DisposeAsync();

            _gateway = null;
        }

        private string Quote(string identifier)
            => (_gateway?.Dialect ?? new SqlDialect(Engine)).Quote(identifier);

        private IReadOnlyList<string> KeyProperties(JobContext context)
            => KeyFields is { Count: > 0 } ? KeyFields : context.Sync.KeyFields;

        private List<(string Property, string Column, object? Value)> KeyValues(Entity entity, JobContext context)
            => KeyProperties(context).Select(p => (p, _mapping.ToField(p), ConvertValue(entity.GetValueOrDefault(p), _columnTypes.GetValueOrDefault(_mapping.ToField(p))))).ToList();

        /// <summary>Columns of the table matching the properties of the entity (the unknown properties are ignored).</summary>
        private List<(string Property, string Column, object? Value)> WritableFields(Entity entity, bool all)
        {
            var result = new List<(string, string, object?)>();
            IEnumerable<string> properties = all || (SyncFields?.Count ?? 0) == 0 ? entity.Properties.Keys : SyncFields!;

            foreach (var property in properties)
            {
                if (_mapping.OnlyMappedFields && !Fields.ContainsKey(property))
                    continue;

                var column = _mapping.ToField(property);
                if (!_columnTypes.TryGetValue(column, out var type) || IsIdentity(property))
                    continue;

                result.Add((property, column, ConvertValue(entity.GetValueOrDefault(property), type)));
            }

            return result;
        }

        private void ConvertToColumnTypes(Entity entity)
        {
            foreach (var property in entity.Properties.Keys.ToList())
            {
                if (_columnTypes.TryGetValue(_mapping.ToField(property), out var type))
                    entity[property] = SqlValues.FromDatabase(ConvertValue(entity[property], type));
            }
        }

        private static object? ConvertValue(object? value, Type? type)
            => SqlValues.ToColumnType(value, type);

        /// <summary>
        /// A failure of the connection makes the target unavailable; a record refused by the database only fails the entity.
        /// </summary>
        private async Task RecordAsync(Func<Task<int>> command)
        {
            try
            {
                await command();
            }
            catch (DbException ex) when (!_gateway!.IsConnectionBroken && !ex.IsTransient)
            {
                throw new SqlRecordException(ex.Message, ex);
            }
        }

        private async Task Guard(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
            {
                throw ex is ArgumentException or PlatformNotSupportedException
                    ? ConnectorGuard.Invalid(DisplayName, $"Chaîne de connexion ou table invalide : {ex.Message}")
                    : ConnectorGuard.TargetUnavailable(DisplayName, Table, ex);
            }
        }
    }

    /// <summary>A record refused by the database (constraint, type, length...): only the entity fails.</summary>
    public sealed class SqlRecordException(string message, Exception inner) : Exception(message, inner);

    internal static class SqlValues
    {
        /// <summary>Database values as entity values (DBNull → null, small integers → long, float → double).</summary>
        public static object? FromDatabase(object? value)
            => value switch
            {
                null or DBNull => null,
                Guid guid => guid.ToString(),
                _ => ValueNormalizer.Normalize(value),
            };

        /// <summary>Converts an entity value to the type of a column; the value is kept as is when it can't be converted.</summary>
        public static object? ToColumnType(object? value, Type? type)
        {
            if (value == null || type == null)
                return value;

            if (value is string s && string.IsNullOrWhiteSpace(s) && type != typeof(string))
                return null;

            object? converted = Type.GetTypeCode(type) switch
            {
                TypeCode.String => ValueConverter.ToText(value),
                TypeCode.Boolean => ValueConverter.ToBoolean(value),
                // Integer columns often hold flags: "oui" / "non" become 1 / 0.
                TypeCode.Byte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
                    => ValueConverter.ToInt64(value) ?? (ValueConverter.ToBoolean(value) is { } flag ? (flag ? 1L : 0L) : null),
                TypeCode.Decimal => ValueConverter.ToDecimal(value),
                TypeCode.Double or TypeCode.Single => ValueConverter.ToDecimal(value) is { } d ? (double)d : null,
                // A DATETIME column keeps no time zone: a local or UTC value is stored, then read back, without its kind. It is
                // compared the same way, otherwise every row would look modified at every run.
                TypeCode.DateTime => ValueConverter.ToDateTime(value) is { } dateTime ? DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified) : null,
                _ when type == typeof(Guid) => Guid.TryParse(ValueConverter.ToText(value), out var guid) ? guid : null,
                _ when type == typeof(DateTimeOffset) => ValueConverter.ToDateTime(value) is { } dt ? new DateTimeOffset(dt) : null,
                _ => value,
            };

            return converted ?? value;
        }
    }
}
