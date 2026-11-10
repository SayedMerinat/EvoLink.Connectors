using System.Collections.Concurrent;
using System.ComponentModel;
using EvoLink.Connectors.Common;
using EvoLink.Odoo.Client;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;
using Ortolan.Core.Sync;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>
    /// Port of the EvoBus Odoo17RecordIdFetcherTransformer: writes in <see cref="TargetProperty"/> the identifier of the record of
    /// <see cref="Model"/> whose field <see cref="OdooField"/> equals the property <see cref="SourceProperty"/> of the entity.
    /// The results (found or not) are cached per value for the run, the cache being shared by the parallel entities.
    /// Not found: warning and null, or failure of the entity with <see cref="FailWhenNotFound"/>.
    /// </summary>
    [OrtolanType("odoo-id-lookup")]
    [Description("Recherche l'identifiant de l'enregistrement Odoo 17 dont un champ vaut une propriété de l'entité et l'écrit dans une propriété (ex. ID du partenaire d'après sa référence).")]
    public class OdooIdLookupTransformer : TransformerModule
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<long?>>> _cache = new(StringComparer.Ordinal);
        private IOdooClient? _client;
        private OdooConnection? _connection;
        private OdooContext _odooContext = OdooContext.Default;
        private JArray _domain = [];

        [Description("URL de l'instance Odoo, ex. https://societe.odoo.com.")]
        public string Url { get; set; } = string.Empty;

        [Description("Base de données Odoo.")]
        public string Database { get; set; } = string.Empty;

        [Description("Utilisateur Odoo (login).")]
        public string Username { get; set; } = string.Empty;

        [Sensitive]
        [Description("Clé API (ou mot de passe) de l'utilisateur Odoo. Peut être une référence '@vault:...'.")]
        public string ApiKey { get; set; } = string.Empty;

        [Description("Délai d'attente d'un appel à Odoo, en secondes.")]
        public int TimeoutSeconds { get; set; } = 100;

        [Description("Modèle Odoo recherché, ex. res.partner.")]
        public string Model { get; set; } = string.Empty;

        [Description("Propriété de l'entité contenant la valeur recherchée.")]
        public string SourceProperty { get; set; } = string.Empty;

        [Description("Champ Odoo comparé à la valeur, ex. ref.")]
        public string OdooField { get; set; } = string.Empty;

        [Description("Propriété de l'entité recevant l'identifiant Odoo trouvé (null si introuvable).")]
        public string TargetProperty { get; set; } = string.Empty;

        [Description("Opérateur de comparaison Odoo : = (exact), =ilike (exact sans casse)...")]
        public string Operator { get; set; } = "=";

        [Description("Filtres supplémentaires du domaine Odoo (ex. company_id = 1).")]
        public List<OdooDomainFilter> Filters { get; set; } = [];

        [Description("Domaine Odoo brut en JSON, ajouté aux filtres.")]
        public string? Domain { get; set; }

        [Description("Langue de la recherche (champs traduisibles), ex. fr_CH.")]
        public string? Language { get; set; }

        [Description("Recherche aussi parmi les enregistrements archivés.")]
        public bool IncludeArchived { get; set; }

        [Description("Fait échouer l'entité lorsqu'aucun enregistrement n'est trouvé (sinon avertissement et valeur null).")]
        public bool FailWhenNotFound { get; set; }

        /// <summary>Client of Odoo, overridable for the tests.</summary>
        protected virtual IOdooClient CreateClient(OdooConnection connection)
            => new OdooJsonRpcClient(connection);

        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            _connection = OdooModuleSupport.CreateConnection(DisplayName, Url, Database, Username, ApiKey, TimeoutSeconds, Model);
            ConnectorGuard.Required(DisplayName, ("sourceProperty", SourceProperty), ("odooField", OdooField), ("targetProperty", TargetProperty));

            _odooContext = OdooModuleSupport.CreateContext(Language, IncludeArchived);
            _domain = OdooDomain.Build(Filters, Domain, DisplayName);
            _client = CreateClient(_connection);

            try
            {
                if (input.Any(e => !e.IsFailed))
                {
                    var fields = await OdooModuleSupport.CallAsync(() => _client.FieldsGetAsync(Model, _odooContext, context.CancellationToken), ToUnavailable);

                    if (!fields.ContainsKey(OdooField.Trim()))
                        throw ConnectorGuard.Invalid(DisplayName, $"Le champ '{OdooField}' n'existe pas dans le modèle Odoo '{Model}'.");
                }

                return await base.ActAsync(input, context);
            }
            finally
            {
                _cache.Clear();
                _client.Dispose();
                _client = null;
            }
        }

        public override async Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            var value = ValueNormalizer.Normalize(entity.GetValueOrDefault(SourceProperty));

            if (ValueConverter.IsEmpty(value))
                return NotFound(entity, $"la propriété '{SourceProperty}' est absente ou vide", context);

            var key = SyncComparer.Canonical(value);
            var lookup = _cache.GetOrAdd(key, _ => new Lazy<Task<long?>>(() => SearchAsync(value, context)));
            long? id;

            try
            {
                id = await lookup.Value;
            }
            catch (Exception)
            {
                // A failed search is not cached: the next entity with the same value tries again.
                _cache.TryRemove(key, out _);
                throw;
            }

            if (id is null)
                return NotFound(entity, $"aucun enregistrement '{Model}' dont '{OdooField}' {Operator} '{ValueConverter.ToText(value)}'", context);

            entity[TargetProperty] = id.Value;
            return entity;
        }

        protected override Exception? TranslateAccessFailure(Exception exception)
            => OdooModuleSupport.IsUnavailability(exception) ? ToUnavailable(exception) : null;

        private async Task<long?> SearchAsync(object? value, JobContext context)
        {
            var client = _client ?? throw new InvalidOperationException("Le client Odoo n'est pas initialisé.");
            var domain = OdooDomain.And(_domain, OdooDomain.Condition(OdooField.Trim(), string.IsNullOrWhiteSpace(Operator) ? "=" : Operator.Trim(), value));
            var ids = await OdooModuleSupport.CallAsync(() => client.SearchAsync(Model, domain, 2, "id", _odooContext, context.CancellationToken), ToUnavailable);

            if (ids.Count > 1)
                context.LogWarning($"[{DisplayName}] Plusieurs enregistrements '{Model}' ont '{OdooField}' = '{ValueConverter.ToText(value)}' : le premier ({ids[0]}) est utilisé.");

            return ids.Count > 0 ? ids[0] : null;
        }

        private Entity NotFound(Entity entity, string reason, JobContext context)
        {
            if (FailWhenNotFound)
                throw new OdooRecordNotFoundException($"Identifiant Odoo introuvable : {reason}.");

            entity[TargetProperty] = null;
            context.LogWarning($"[{DisplayName}] Identifiant Odoo introuvable ({reason}) pour {entity.ToFlatString(200)}.");

            return entity;
        }

        private Ortolan.Core.Exceptions.SourceUnavailableException ToUnavailable(Exception exception)
            => ConnectorGuard.SourceUnavailable(DisplayName, _connection?.ToString() ?? Url, exception);
    }

    /// <summary>No Odoo record matches the searched value.</summary>
    public sealed class OdooRecordNotFoundException : Exception
    {
        public OdooRecordNotFoundException(string message) : base(message) { }
    }
}
