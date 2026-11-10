using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using EvoLink.Connectors.Common;
using EvoLink.Odoo.Client;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;
using Ortolan.Core.Sync;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>
    /// Synchronises the entities with an Odoo 17 model through the native JSON-RPC client
    /// (<c>search_read</c> / <c>create</c> / <c>write</c> / <c>unlink</c>).
    /// <list type="bullet">
    /// <item>Only the properties matching a writable field of the model are compared and written; <c>id</c>, the magic fields,
    /// the one2many and the computed fields without inverse never are.</item>
    /// <item>The source values are converted to the type of their field (dates, numbers, booleans, relations) before the
    /// comparison, on a copy of the entity: the entities leaving the module keep their original values.</item>
    /// <item>many2one / many2many accept identifiers or, with <see cref="ResolveRelationsByName"/>, names resolved by <c>name_search</c>.</item>
    /// <item>An update only writes the fields that differ. Deletion is an <c>unlink</c> or, with <see cref="ArchiveInsteadOfDelete"/>, an archiving.</item>
    /// <item>The Odoo identifier is kept in the metadata <see cref="IdMetadataKey"/>.</item>
    /// </list>
    /// </summary>
    [OrtolanType("odoo-synchroniser")]
    [Description("Synchronise les entités avec un modèle Odoo 17 (JSON-RPC natif) : lit la cible, crée, met à jour (champs modifiés seulement) et supprime ou archive les enregistrements.")]
    public class OdooSynchroniser : SynchroniserModule
    {
        public const string IdMetadataKey = "odoo.id";
        public const string ModelMetadataKey = "odoo.model";

        private readonly ConcurrentDictionary<string, Lazy<Task<long?>>> _relations = new(StringComparer.Ordinal);
        private IOdooClient? _client;
        private OdooConnection? _connection;
        private OdooContext _odooContext = OdooContext.Default;
        private TimeZoneInfo _timeZone = TimeZoneInfo.Local;
        private JArray _domain = [];
        private IReadOnlyDictionary<string, OdooFieldInfo> _modelFields = new Dictionary<string, OdooFieldInfo>();
        private Dictionary<string, OdooFieldInfo> _fieldsByProperty = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _keyProperties = [];
        private List<string> _writableProperties = [];
        private CancellationToken _cancellationToken;
        private JobContext? _jobContext;

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

        [Description("Modèle Odoo synchronisé, ex. res.partner.")]
        public string Model { get; set; } = string.Empty;

        [Description("Filtres délimitant les enregistrements de la cible lus et gérés (ex. company_id = 1). En mode miroir, seuls ces enregistrements peuvent être supprimés.")]
        public List<OdooDomainFilter> Filters { get; set; } = [];

        [Description("Domaine Odoo brut en JSON, ajouté aux filtres (permet les « ou »).")]
        public string? Domain { get; set; }

        [Description("Correspondance propriété de l'entité → champ Odoo (vide = mêmes noms).")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("N'écrit que les propriétés présentes dans la correspondance (les champs clés restent toujours utilisés).")]
        public bool OnlyMappedFields { get; set; }

        [Description("Archive l'enregistrement (active = false) au lieu de le supprimer.")]
        public bool ArchiveInsteadOfDelete { get; set; }

        [Description("Les many2one / many2many donnés sous forme de texte sont recherchés par nom (name_search) dans le modèle lié ; sinon seuls des identifiants sont acceptés.")]
        public bool ResolveRelationsByName { get; set; }

        [Description("Opérateur de la recherche par nom des relations : = (exact), =ilike (exact sans casse) ou ilike (contient).")]
        public string RelationSearchOperator { get; set; } = "=";

        [Description("Fait échouer l'entité lorsqu'une relation nommée est introuvable (sinon avertissement et champ non écrit pour cet enregistrement).")]
        public bool FailWhenNotFound { get; set; }

        [Description("Langue des champs traduisibles lus et écrits, ex. fr_CH (vide = langue de l'utilisateur).")]
        public string? Language { get; set; }

        [Description("Lit aussi les enregistrements archivés de la cible (un enregistrement archivé est alors mis à jour au lieu d'être recréé).")]
        public bool IncludeArchived { get; set; }

        [Description("Fuseau horaire des dates et heures sans fuseau de la source (Odoo les stocke en UTC), ex. Europe/Zurich. Vide = fuseau du serveur Ortolan.")]
        public string? TimeZone { get; set; }

        [Description("Nombre d'enregistrements lus par appel lors de la lecture de la cible.")]
        public int PageSize { get; set; } = 500;

        /// <summary>Client of the target, overridable for the tests.</summary>
        protected virtual IOdooClient CreateClient(OdooConnection connection)
            => new OdooJsonRpcClient(connection);

        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            _connection = OdooModuleSupport.CreateConnection(DisplayName, Url, Database, Username, ApiKey, TimeoutSeconds, Model);
            _timeZone = OdooModuleSupport.ResolveTimeZone(DisplayName, TimeZone);
            _odooContext = OdooModuleSupport.CreateContext(Language, IncludeArchived);
            _domain = OdooDomain.Build(Filters, Domain, DisplayName);
            _cancellationToken = context.CancellationToken;
            _jobContext = context;

            var keyFields = KeyFields is { Count: > 0 } ? KeyFields : context.Sync.KeyFields;
            if (keyFields.Count == 0)
                throw new OrtolanConfigurationException($"[{DisplayName}] Aucun champ clé n'est configuré : impossible de garantir l'unicité des enregistrements.");

            var configured = SyncFields;
            _client = CreateClient(_connection);

            try
            {
                _modelFields = await CallAsync(() => Client.FieldsGetAsync(Model, _odooContext, _cancellationToken));

                if (_modelFields.Count == 0)
                    throw ConnectorGuard.Invalid(DisplayName, $"Le modèle Odoo '{Model}' est introuvable ou n'a aucun champ accessible.");

                if (ArchiveInsteadOfDelete && !_modelFields.ContainsKey("active"))
                    throw ConnectorGuard.Invalid(DisplayName, $"Le modèle Odoo '{Model}' n'a pas de champ 'active' : impossible d'archiver.");

                ResolveProperties(input, keyFields, configured ?? context.Sync.SyncFields, context);

                var originals = new Dictionary<Entity, Entity>(ReferenceEqualityComparer.Instance);
                var prepared = await PrepareAsync(input, originals, context);

                // The base class compares the sync fields: they are limited to the writable fields of the model, otherwise
                // a property unknown to Odoo would always look different and the record would be updated at every run.
                SyncFields = _writableProperties;

                var output = await base.ActAsync(prepared, context);

                return output.Select(e => Restore(e, originals)).ToList();
            }
            finally
            {
                SyncFields = configured;
                _relations.Clear();
                _client.Dispose();
                _client = null;
                _jobContext = null;
            }
        }

        protected override async Task<IEnumerable<Entity>> ReadTargetAsync(JobContext context)
        {
            var fields = _fieldsByProperty.Values.Select(f => f.Name).Prepend("id").Distinct(StringComparer.Ordinal).ToList();
            var records = await CallAsync(() => Client.SearchReadAllAsync(Model, _domain, fields, PageSize, 0, "id", _odooContext, _cancellationToken));

            return records.Select(ToEntity).ToList();
        }

        protected override async Task<Entity> CreateAsync(Entity source, JobContext context)
        {
            var values = ToValues(source, _keyProperties.Concat(_writableProperties));
            var ids = await CallAsync(() => Client.CreateAsync(Model, [values], _odooContext, _cancellationToken));

            source.Metadata[IdMetadataKey] = ids[0].ToString(CultureInfo.InvariantCulture);
            source.Metadata[ModelMetadataKey] = Model;

            return source;
        }

        protected override async Task<Entity> UpdateAsync(Entity source, Entity existing, JobContext context)
        {
            var id = GetId(existing);
            var changed = SyncComparer.GetChangedFields(source, existing, _writableProperties).Where(source.Has).ToList();
            var values = ToValues(source, changed);

            if (values.Count > 0)
                await CallAsync(() => Client.WriteAsync(Model, [id], values, _odooContext, _cancellationToken));
            else
                context.LogDebug($"[{DisplayName}] Aucun champ à écrire pour l'enregistrement Odoo {id}.");

            source.Metadata[IdMetadataKey] = id.ToString(CultureInfo.InvariantCulture);
            source.Metadata[ModelMetadataKey] = Model;

            return source;
        }

        protected override async Task DeleteAsync(Entity existing, JobContext context)
        {
            var id = GetId(existing);

            if (ArchiveInsteadOfDelete)
                await CallAsync(() => Client.WriteAsync(Model, [id], new Dictionary<string, object?> { ["active"] = false }, _odooContext, _cancellationToken));
            else
                await CallAsync(() => Client.UnlinkAsync(Model, [id], _odooContext, _cancellationToken));
        }

        private IOdooClient Client
            => _client ?? throw new InvalidOperationException("Le client Odoo n'est pas initialisé.");

        /// <summary>
        /// Odoo field of every key property and of every writable synchronised property.
        /// </summary>
        private void ResolveProperties(List<Entity> input, IReadOnlyList<string> keyFields, IReadOnlyList<string> configured, JobContext context)
        {
            var mapping = new FieldMapping(Fields, OnlyMappedFields);
            var mapped = new HashSet<string>(Fields.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()), StringComparer.OrdinalIgnoreCase);

            _fieldsByProperty = new Dictionary<string, OdooFieldInfo>(StringComparer.OrdinalIgnoreCase);
            _keyProperties = [];
            _writableProperties = [];

            foreach (var key in keyFields)
            {
                var field = mapping.ToField(key);

                if (!_modelFields.TryGetValue(field, out var info))
                    throw ConnectorGuard.Invalid(DisplayName, $"Le champ clé '{key}' (champ Odoo '{field}') n'existe pas dans le modèle Odoo '{Model}'.");

                _fieldsByProperty[key] = info;
                _keyProperties.Add(key);
            }

            var requested = configured.Count > 0
                ? configured.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : input.Where(e => e.SyncAction != SyncAction.Delete && !e.IsFailed).SelectMany(e => e.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var ignored = new List<string>();

            foreach (var property in requested)
            {
                if (mapping.OnlyMappedFields && !mapped.Contains(property) && !_keyProperties.Contains(property, StringComparer.OrdinalIgnoreCase))
                {
                    ignored.Add(property);
                    continue;
                }

                if (_modelFields.TryGetValue(mapping.ToField(property), out var info) && OdooValues.IsWritable(info))
                {
                    _fieldsByProperty[property] = info;
                    _writableProperties.Add(property);
                }
                else if (!_keyProperties.Contains(property, StringComparer.OrdinalIgnoreCase))
                {
                    ignored.Add(property);
                }
            }

            if (ignored.Count > 0)
            {
                var message = $"[{DisplayName}] Propriété(s) ignorée(s), absentes du modèle Odoo '{Model}' ou non modifiables : {string.Join(", ", ignored)}.";

                if (configured.Count > 0)
                    context.LogWarning(message);
                else
                    context.LogDebug(message);
            }

            if (_writableProperties.Count == 0 && input.Any(e => e.SyncAction != SyncAction.Delete && !e.IsFailed))
                throw ConnectorGuard.Invalid(DisplayName, $"Aucune propriété des entités ne correspond à un champ modifiable du modèle Odoo '{Model}'.");
        }

        /// <summary>
        /// Copies of the entities whose Odoo properties are converted to the type of their field. An entity whose value can't be
        /// converted fails (the job continues), the copies are mapped to their original in <paramref name="originals"/>.
        /// </summary>
        private async Task<List<Entity>> PrepareAsync(List<Entity> input, Dictionary<Entity, Entity> originals, JobContext context)
        {
            var prepared = new List<Entity>(input.Count);

            foreach (var entity in input)
            {
                if (entity.IsFailed)
                {
                    prepared.Add(entity);
                    continue;
                }

                var copy = entity.Clone();
                var properties = entity.SyncAction == SyncAction.Delete ? _keyProperties : _fieldsByProperty.Keys.ToList();

                try
                {
                    foreach (var property in properties)
                    {
                        if (!copy.TryGetValue(property, out var value))
                            continue;

                        var field = _fieldsByProperty[property];

                        try
                        {
                            copy[property] = await OdooValues.ToComparableAsync(value, field, _timeZone, ResolveRelationsByName ? ResolveByNameAsync : null);
                        }
                        catch (OdooRelationNotFoundException ex) when (!FailWhenNotFound && !_keyProperties.Contains(property, StringComparer.OrdinalIgnoreCase))
                        {
                            context.LogWarning($"[{DisplayName}] {ex.Message} Le champ '{field.Name}' n'est pas écrit pour {entity.ToFlatString(200)}.");
                            copy.Remove(property);
                        }
                    }
                }
                catch (Exception ex) when (ContinueOnError && !ExceptionClassifier.IsFatalForJob(ex) && !OdooModuleSupport.IsUnavailability(ex))
                {
                    entity.SyncResult = SyncResult.Failed;
                    entity.Error = ex.Message;
                    context.Report.Increment(SyncResult.Failed);
                    context.LogError($"[{DisplayName}] Échec de la préparation de {entity.ToFlatString(200)} : {ex.Message}");
                    prepared.Add(entity);
                    continue;
                }

                originals[copy] = entity;
                prepared.Add(copy);
            }

            return prepared;
        }

        /// <summary>
        /// Identifier of the record of <paramref name="model"/> named <paramref name="name"/>, cached for the run.
        /// </summary>
        private async Task<long?> ResolveByNameAsync(string model, string name)
        {
            var key = model + "\u001F" + name;
            var lookup = _relations.GetOrAdd(key, _ => new Lazy<Task<long?>>(() => SearchByNameAsync(model, name)));

            try
            {
                return await lookup.Value;
            }
            catch (Exception)
            {
                _relations.TryRemove(key, out _);
                throw;
            }
        }

        private async Task<long?> SearchByNameAsync(string model, string name)
        {
            var results = await CallAsync(() => Client.NameSearchAsync(model, name, RelationSearchOperator, null, 2, _odooContext, _cancellationToken));

            if (results.Count > 1)
                _jobContext?.LogWarning($"[{DisplayName}] Plusieurs enregistrements '{model}' correspondent au nom '{name}' : le premier ({results[0].Id}) est utilisé.");

            return results.Count > 0 ? results[0].Id : null;
        }

        private Entity ToEntity(Dictionary<string, object?> record)
        {
            var entity = new Entity();

            foreach (var (property, field) in _fieldsByProperty)
                entity[property] = OdooValues.FromOdoo(record.GetValueOrDefault(field.Name), field.Type);

            if (ValueConverter.ToInt64(record.GetValueOrDefault("id")) is { } id)
                entity.Metadata[IdMetadataKey] = id.ToString(CultureInfo.InvariantCulture);

            entity.Metadata[ModelMetadataKey] = Model;

            return entity;
        }

        /// <summary>Odoo values of the given (converted) properties of the source, limited to the writable fields.</summary>
        private Dictionary<string, object?> ToValues(Entity source, IEnumerable<string> properties)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var property in properties.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!source.TryGetValue(property, out var value) || !_fieldsByProperty.TryGetValue(property, out var field) || !OdooValues.IsWritable(field))
                    continue;

                values[field.Name] = OdooValues.ToPayload(value, field);
            }

            return values;
        }

        /// <summary>The original entity receives the result of its copy (status, error, Odoo identifier).</summary>
        private static Entity Restore(Entity processed, Dictionary<Entity, Entity> originals)
        {
            if (!originals.TryGetValue(processed, out var original))
                return processed;

            original.SyncResult = processed.SyncResult;
            original.Error = processed.Error;

            foreach (var (key, value) in processed.Metadata)
                original.Metadata[key] = value;

            return original;
        }

        private long GetId(Entity existing)
        {
            if (existing.Metadata.TryGetValue(IdMetadataKey, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                return id;

            throw new OrtolanException($"[{DisplayName}] Identifiant Odoo absent de l'enregistrement {existing.ToFlatString(200)}.");
        }

        private Task<T> CallAsync<T>(Func<Task<T>> call)
            => OdooModuleSupport.CallAsync(call, ToUnavailable);

        private Task CallAsync(Func<Task> call)
            => OdooModuleSupport.CallAsync(call, ToUnavailable);

        private TargetUnavailableException ToUnavailable(Exception exception)
            => ConnectorGuard.TargetUnavailable(DisplayName, _connection?.ToString() ?? Url, exception);
    }
}
