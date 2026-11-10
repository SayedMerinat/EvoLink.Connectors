using System.ComponentModel;
using System.Globalization;
using EvoLink.Connectors.Common;
using EvoLink.Odoo.Client;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>
    /// Reads the records of an Odoo 17 model (<c>search_read</c> paginated) through the JSON-RPC client.
    /// <list type="bullet">
    /// <item>The values are normalised: <c>false</c> of an empty field becomes null, a many2one becomes its id, dates become <see cref="DateTime"/>.</item>
    /// <item>Incremental reading: only the records whose <c>write_date</c> is later than the last successful run (minus a margin)
    /// are read, in incremental mode only (a mirror or full synchronisation needs every record).</item>
    /// <item>The Odoo identifier is kept in the metadata <see cref="OdooSynchroniser.IdMetadataKey"/>.</item>
    /// </list>
    /// </summary>
    [OrtolanType("odoo-provider")]
    [Description("Lit les enregistrements d'un modèle Odoo 17 (JSON-RPC natif), avec filtres de domaine, choix des champs et lecture incrémentale.")]
    public class OdooProvider : ProviderModule
    {
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

        [Description("Modèle Odoo lu, ex. res.partner.")]
        public string Model { get; set; } = string.Empty;

        [Description("Filtres du domaine Odoo (tous doivent être vérifiés), ex. { \"field\": \"is_company\", \"operator\": \"=\", \"value\": true }.")]
        public List<OdooDomainFilter> Filters { get; set; } = [];

        [Description("Domaine Odoo brut en JSON, ajouté aux filtres (permet les « ou »), ex. [\"|\", [\"city\", \"=\", \"Sion\"], [\"city\", \"=\", \"Sierre\"]].")]
        public string? Domain { get; set; }

        [Description("Champs Odoo lus (vide = les champs de la correspondance si onlyMappedFields, sinon tous les champs du modèle).")]
        public List<string> ReadFields { get; set; } = [];

        [Description("Correspondance propriété de l'entité → champ Odoo (vide = mêmes noms).")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("Ne garde que les champs présents dans la correspondance.")]
        public bool OnlyMappedFields { get; set; }

        [Description("Ajoute pour chaque many2one une propriété '<champ>_name' contenant le nom de l'enregistrement lié.")]
        public bool IncludeMany2OneNames { get; set; }

        [Description("Langue des champs traduisibles, ex. fr_CH (vide = langue de l'utilisateur).")]
        public string? Language { get; set; }

        [Description("Lit aussi les enregistrements archivés (active = false).")]
        public bool IncludeArchived { get; set; }

        [Description("Fuseau horaire des dates et heures renvoyées (Odoo les stocke en UTC), ex. Europe/Zurich. Vide = fuseau du serveur Ortolan.")]
        public string? TimeZone { get; set; }

        [Description("Ordre de lecture Odoo, ex. \"id\" ou \"name asc\".")]
        public string Order { get; set; } = "id";

        [Description("Nombre d'enregistrements lus par appel (pagination).")]
        public int PageSize { get; set; } = 500;

        [Description("Nombre maximal d'enregistrements lus (0 = tous).")]
        public int Limit { get; set; }

        [Description("Lecture incrémentale : en mode incrémental, ne lit que les enregistrements modifiés depuis la dernière synchronisation réussie.")]
        public bool Incremental { get; set; }

        [Description("Champ de date de modification utilisé par la lecture incrémentale.")]
        public string IncrementalField { get; set; } = "write_date";

        [Description("Marge (minutes) retranchée à la date de la dernière synchronisation réussie, pour ne manquer aucune modification faite pendant le job précédent.")]
        public int IncrementalMarginMinutes { get; set; } = 10;

        /// <summary>Client of the source, overridable for the tests.</summary>
        protected virtual IOdooClient CreateClient(OdooConnection connection)
            => new OdooJsonRpcClient(connection);

        public override async Task<List<Entity>> FetchAsync(JobContext context)
        {
            var connection = OdooModuleSupport.CreateConnection(DisplayName, Url, Database, Username, ApiKey, TimeoutSeconds, Model);
            var timeZone = OdooModuleSupport.ResolveTimeZone(DisplayName, TimeZone);
            var odooContext = OdooModuleSupport.CreateContext(Language, IncludeArchived);
            var domain = OdooDomain.Build(Filters, Domain, DisplayName);
            var mapping = new FieldMapping(Fields, OnlyMappedFields);

            if (Incremental && string.IsNullOrWhiteSpace(IncrementalField))
                throw ConnectorGuard.Invalid(DisplayName, "Le champ de la lecture incrémentale (incrementalField) est vide.");

            using var client = CreateClient(connection);

            var modelFields = await CallAsync(() => client.FieldsGetAsync(Model, odooContext, context.CancellationToken), connection);

            if (modelFields.Count == 0)
                throw ConnectorGuard.Invalid(DisplayName, $"Le modèle Odoo '{Model}' est introuvable ou n'a aucun champ accessible.");

            var readFields = GetReadFields(modelFields);

            if (Incremental)
                domain = AddIncrementalCriterion(domain, modelFields, context);

            var records = await CallAsync(() => client.SearchReadAllAsync(Model, domain, readFields, PageSize, Limit, Order, odooContext, context.CancellationToken), connection);

            return records.Select(r => ToEntity(r, modelFields, mapping, timeZone)).ToList();
        }

        private List<string>? GetReadFields(IReadOnlyDictionary<string, OdooFieldInfo> modelFields)
        {
            var requested = ReadFields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();

            if (requested.Count == 0 && OnlyMappedFields && Fields.Count > 0)
                requested = Fields.Values.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();

            if (requested.Count == 0)
                return null;

            var unknown = requested.Where(f => !modelFields.ContainsKey(f)).ToList();
            if (unknown.Count > 0)
                throw ConnectorGuard.Invalid(DisplayName, $"Champ(s) absent(s) du modèle Odoo '{Model}' : {string.Join(", ", unknown)}.");

            return requested.Prepend("id").Distinct(StringComparer.Ordinal).ToList();
        }

        private Newtonsoft.Json.Linq.JArray AddIncrementalCriterion(Newtonsoft.Json.Linq.JArray domain, IReadOnlyDictionary<string, OdooFieldInfo> modelFields, JobContext context)
        {
            var field = IncrementalField.Trim();

            if (!modelFields.TryGetValue(field, out var info) || info.Type is not ("datetime" or "date"))
                throw ConnectorGuard.Invalid(DisplayName, $"Le champ '{field}' de la lecture incrémentale n'est pas un champ date du modèle Odoo '{Model}'.");

            if (context.Mode != SyncMode.Incremental)
            {
                context.LogInfo($"[{DisplayName}] Lecture incrémentale ignorée en mode {context.Mode} : tous les enregistrements sont lus.");
                return domain;
            }

            if (context.LastSuccessfulRun is not { } lastRun)
            {
                context.LogInfo($"[{DisplayName}] Aucune synchronisation réussie précédente : tous les enregistrements sont lus.");
                return domain;
            }

            var since = lastRun.UtcDateTime.AddMinutes(-Math.Max(0, IncrementalMarginMinutes));
            var value = since.ToString(info.Type == "date" ? OdooValues.DateFormat : OdooValues.DateTimeFormat, CultureInfo.InvariantCulture);

            context.LogInfo($"[{DisplayName}] Lecture incrémentale : enregistrements dont '{field}' > {value} (UTC).");

            return OdooDomain.And(domain, OdooDomain.Condition(field, info.Type == "date" ? ">=" : ">", value));
        }

        private Entity ToEntity(Dictionary<string, object?> record, IReadOnlyDictionary<string, OdooFieldInfo> modelFields, FieldMapping mapping, TimeZoneInfo timeZone)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var (field, value) in record)
            {
                var type = modelFields.TryGetValue(field, out var info) ? info.Type : null;
                values[field] = OdooValues.FromOdooForEntity(value, type, timeZone);

                if (IncludeMany2OneNames && type == "many2one")
                    values[field + "_name"] = value is System.Collections.IList { Count: > 1 } pair ? pair[1] : null;
            }

            var entity = mapping.ToEntity(values);
            entity.Metadata[OdooSynchroniser.ModelMetadataKey] = Model;

            if (ValueConverter.ToInt64(record.GetValueOrDefault("id")) is { } id)
                entity.Metadata[OdooSynchroniser.IdMetadataKey] = id.ToString(CultureInfo.InvariantCulture);

            return entity;
        }

        private Task<T> CallAsync<T>(Func<Task<T>> call, OdooConnection connection)
            => OdooModuleSupport.CallAsync(call, ex => ConnectorGuard.SourceUnavailable(DisplayName, connection.ToString(), ex));
    }
}
