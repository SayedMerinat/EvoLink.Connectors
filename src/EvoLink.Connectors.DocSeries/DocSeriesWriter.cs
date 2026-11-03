using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.DocSeries
{
    /// <summary>How an entity value is compared to the items of a DocSeries list.</summary>
    public enum ListItemComparison
    {
        /// <summary>The item must be equal to the value.</summary>
        Equals,

        /// <summary>The first item containing the value is used.</summary>
        Contains,
    }

    /// <summary>
    /// Native DocSeries writer, replacing the EvoBus <c>DocSeriesDataWriter</c>: creates one document per entity
    /// (content type, metadata, attached file). The API <c>EvoLink.DocSeries.API</c> 1.1.0 can create documents but can't
    /// update their metadata nor delete them, hence a writer and not a synchroniser.
    /// <para>
    /// Values of list fields (static lists, internal / external tables) are matched against the items of the list with
    /// <see cref="ListItemComparison"/>; missing items of internal tables can be created (<see cref="CreateListItemIfMissing"/>).
    /// </para>
    /// Differences with EvoBus: password protected by Ortolan ([Sensitive], "@vault:"), field mapping, fixed or per-entity
    /// content type, every error fails the entity instead of being logged (missing file, unknown content type or field),
    /// the matched item of a static list is really written, unreachable server → target unavailability (restart policy).
    /// </summary>
    [OrtolanType("docseries-writer")]
    [Description("Crée un document DocSeries par entité (type de contenu, métadonnées, listes, fichier joint).")]
    public class DocSeriesWriter : WriterModule
    {
        /// <summary>Metadata key of the entity receiving the identifier of the created document.</summary>
        public const string ObjectIdMetadata = "DocSeriesObjectId";

        private readonly ConcurrentDictionary<string, Lazy<Task<ContentTypeInfo>>> _contentTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, Lazy<Task<IReadOnlyList<string>>>> _lists = new();
        private readonly ConcurrentDictionary<int, Lazy<Task<string>>> _internalColumns = new();
        private readonly SemaphoreSlim _listCreation = new(1, 1);
        private IDocSeriesGateway? _gateway;
        private IReadOnlyList<DocSeriesContentType> _availableContentTypes = [];

        [Description("Adresse du serveur DocSeries (ex. https://docseries.client.ch).")]
        public string Host { get; set; } = string.Empty;

        [Description("Utilisateur DocSeries.")]
        public string Username { get; set; } = string.Empty;

        [Sensitive]
        [Description("Mot de passe DocSeries (chiffré à l'enregistrement, accepte une référence '@vault:...').")]
        public string Password { get; set; } = string.Empty;

        [Description("Type de contenu (nom ou identifiant) de tous les documents ; vide = lu dans la propriété 'contentTypeProperty' de chaque entité.")]
        public string? ContentType { get; set; }

        [Description("Propriété de l'entité contenant le type de contenu lorsque 'contentType' est vide (DocType comme EvoBus).")]
        public string ContentTypeProperty { get; set; } = "DocType";

        [Description("Propriété de l'entité contenant le chemin du fichier à joindre (FILE-FilePath comme EvoBus).")]
        public string FilePathProperty { get; set; } = "FILE-FilePath";

        [Description("Met l'entité en échec lorsqu'elle n'a pas de fichier à joindre (sinon le document est créé sans fichier).")]
        public bool RequireFile { get; set; } = true;

        [Description("Correspondance propriété de l'entité → champ DocSeries (vide = propriétés dont le nom est celui d'un champ du type de contenu).")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("N'écrit que les propriétés présentes dans 'fields'.")]
        public bool OnlyMappedFields { get; set; }

        [Description("Comparaison des valeurs avec les éléments des listes : Equals (égalité) ou Contains (premier élément contenant la valeur).")]
        public ListItemComparison ListItemComparison { get; set; } = ListItemComparison.Equals;

        [Description("Compare les éléments de liste sans tenir compte de la casse.")]
        public bool ListItemIgnoreCase { get; set; }

        [Description("Crée l'élément manquant dans les listes de type table interne (les tables externes et listes statiques ne peuvent pas être complétées).")]
        public bool CreateListItemIfMissing { get; set; }

        [Description("Met l'entité en échec lorsqu'une valeur de liste est introuvable (sinon avertissement et champ non renseigné).")]
        public bool FailWhenNotFound { get; set; }

        [Description("Format des dates envoyées à DocSeries.")]
        public string DateFormat { get; set; } = "dd.MM.yyyy";

        [Description("Envoie aussi les champs du type de contenu non renseignés (vides), comme EvoBus.")]
        public bool AutomaticEmptyFields { get; set; } = true;

        [Description("Propriété de l'entité recevant l'identifiant du document créé (vide = métadonnée DocSeriesObjectId uniquement).")]
        public string? ObjectIdProperty { get; set; }

        private IDocSeriesGateway Gateway => _gateway ?? throw new InvalidOperationException($"[{DisplayName}] La connexion à DocSeries n'est pas ouverte.");

        /// <summary>Creates the access to DocSeries (overridden by the tests with an in-memory fake).</summary>
        protected virtual IDocSeriesGateway CreateGateway()
            => new DocSeriesGateway(Host.Trim(), Username, Password);

        protected override async Task BeforeEntitiesAsync(List<Entity> input, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("host", Host), ("username", Username), ("password", Password));

            if (string.IsNullOrWhiteSpace(ContentType))
                ConnectorGuard.Required(DisplayName, ("contentTypeProperty", ContentTypeProperty));

            if (!Uri.TryCreate(Host.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw ConnectorGuard.Invalid(DisplayName, $"Adresse du serveur DocSeries '{Host}' invalide (http:// ou https:// attendu).");

            _contentTypes.Clear();
            _lists.Clear();
            _internalColumns.Clear();
            _gateway = CreateGateway();

            // Reads the content types once: checks the connection and the credentials before processing the entities.
            _availableContentTypes = await ConnectorGuard.CallAsync(
                () => _gateway.GetContentTypesAsync(),
                _ => false,
                ex => ConnectorGuard.TargetUnavailable(DisplayName, Host, ex));

            context.LogDebug($"[{DisplayName}] Connecté à DocSeries '{Host}' ({_availableContentTypes.Count} type(s) de contenu).");
        }

        public override async Task<Entity> WriteAsync(Entity entity, JobContext context)
        {
            var contentType = await GetContentTypeAsync(ResolveContentTypeName(entity));
            var fields = await BuildFieldsAsync(entity, contentType, context);
            var filePath = ResolveFile(entity, context);

            var objectId = await Gateway.CreateDocumentAsync(new DocSeriesNewDocument(contentType.Type.Name, fields, filePath, AutomaticEmptyFields));

            entity.Metadata[ObjectIdMetadata] = objectId.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(ObjectIdProperty))
                entity[ObjectIdProperty] = (long)objectId;

            context.LogDebug($"[{DisplayName}] Document {objectId} créé (type '{contentType.Type.Name}', {fields.Count} champ(s){(filePath == null ? ", sans fichier" : string.Empty)}).");

            return entity;
        }

        private string ResolveContentTypeName(Entity entity)
        {
            if (!string.IsNullOrWhiteSpace(ContentType))
                return ContentType.Trim();

            var name = ValueConverter.ToText(entity.GetValueOrDefault(ContentTypeProperty))?.Trim();

            return string.IsNullOrEmpty(name)
                ? throw new InvalidOperationException($"La propriété '{ContentTypeProperty}' (type de contenu DocSeries) est absente ou vide.")
                : name;
        }

        private Task<ContentTypeInfo> GetContentTypeAsync(string nameOrId)
            => GetOrAddAsync(_contentTypes, nameOrId, async () =>
            {
                var type = _availableContentTypes.FirstOrDefault(t => string.Equals(t.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
                    ?? (int.TryParse(nameOrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? _availableContentTypes.FirstOrDefault(t => t.Id == id) : null)
                    ?? throw new DocSeriesRequestException($"Type de contenu DocSeries '{nameOrId}' introuvable.");

                return new ContentTypeInfo(type, await Gateway.GetFieldsAsync(type.Id));
            });

        private async Task<Dictionary<string, string>> BuildFieldsAsync(Entity entity, ContentTypeInfo contentType, JobContext context)
        {
            var mapping = new FieldMapping(Fields, OnlyMappedFields);
            var mappedProperties = Fields.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (property, value) in entity.Properties)
            {
                var mapped = mappedProperties.Contains(property);

                if (mapping.OnlyMappedFields && !mapped)
                    continue;

                var fieldName = mapping.ToField(property);
                var definition = contentType.Find(fieldName);

                if (definition == null)
                {
                    if (mapped)
                        throw new DocSeriesRequestException($"Le champ '{fieldName}' n'existe pas dans le type de contenu '{contentType.Type.Name}'.");

                    continue;
                }

                var text = ToText(value);
                if (string.IsNullOrEmpty(text))
                {
                    context.LogDebug($"[{DisplayName}] Champ '{definition.Name}' non renseigné : valeur vide.");
                    continue;
                }

                if (definition.IsDynamicList || definition.IsStaticList)
                {
                    text = await ResolveListValueAsync(contentType, definition, text, context);
                    if (text == null)
                        continue;
                }

                fields[definition.Name] = text;
            }

            return fields;
        }

        private string? ToText(object? value)
            => value switch
            {
                DateTime date => date.ToString(DateFormat, CultureInfo.InvariantCulture),
                DateTimeOffset date => date.LocalDateTime.ToString(DateFormat, CultureInfo.InvariantCulture),
                _ => ValueConverter.ToText(value)?.Trim(),
            };

        /// <summary>
        /// Matches a value with the items of a list field. Returns the item to write, or null when the field must be left empty.
        /// </summary>
        private async Task<string?> ResolveListValueAsync(ContentTypeInfo contentType, DocSeriesFieldDefinition field, string value, JobContext context)
        {
            var items = await GetListAsync(contentType, field);

            if (FindItem(items, value) is { } match)
                return match;

            if (CreateListItemIfMissing && field.FieldType == DocSeriesFieldDefinition.InternalTableType)
            {
                await CreateListItemAsync(contentType, field, value, context);
                return value;
            }

            var reason = field.FieldType == DocSeriesFieldDefinition.InternalTableType || !CreateListItemIfMissing
                ? string.Empty
                : " (les éléments ne peuvent être créés que dans les tables internes)";
            var message = $"[{DisplayName}] Aucun élément de la liste '{field.Name}' ne correspond à la valeur '{value}'{reason}.";

            if (FailWhenNotFound)
                throw new KeyNotFoundException(message);

            context.LogWarning(message + " Le champ n'est pas renseigné.");
            return null;
        }

        private string? FindItem(IReadOnlyList<string> items, string value)
        {
            var comparison = ListItemIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return ListItemComparison == ListItemComparison.Contains
                ? items.FirstOrDefault(i => i.Contains(value, comparison))
                : items.FirstOrDefault(i => string.Equals(i, value, comparison));
        }

        private Task<IReadOnlyList<string>> GetListAsync(ContentTypeInfo contentType, DocSeriesFieldDefinition field)
            => GetOrAddAsync(_lists, field.Id, () => field.IsStaticList
                ? Gateway.GetStaticListAsync(contentType.Type.Id, field)
                : Gateway.GetDynamicListAsync(field.Id));

        private async Task CreateListItemAsync(ContentTypeInfo contentType, DocSeriesFieldDefinition field, string value, JobContext context)
        {
            // Serialised so that two entities with the same new value don't create it twice.
            await _listCreation.WaitAsync(context.CancellationToken);
            try
            {
                if (FindItem(await GetListAsync(contentType, field), value) != null)
                    return;

                var column = await GetOrAddAsync(_internalColumns, field.Id, () => Gateway.GetInternalTableColumnAsync(field.Id));
                await Gateway.AddDynamicListItemAsync(field.Id, column, value);

                var items = await GetListAsync(contentType, field);
                _lists[field.Id] = new Lazy<Task<IReadOnlyList<string>>>(Task.FromResult<IReadOnlyList<string>>([.. items, value]));

                context.LogInfo($"[{DisplayName}] Élément '{value}' créé dans la liste '{field.Name}'.");
            }
            finally
            {
                _listCreation.Release();
            }
        }

        private string? ResolveFile(Entity entity, JobContext context)
        {
            var path = ValueConverter.ToText(entity.GetValueOrDefault(FilePathProperty))?.Trim();

            if (string.IsNullOrEmpty(path))
            {
                if (RequireFile)
                    throw new InvalidOperationException($"La propriété '{FilePathProperty}' (fichier à joindre) est absente ou vide.");

                return null;
            }

            var resolved = context.ResolvePath(path);

            return File.Exists(resolved)
                ? resolved
                : throw new InvalidOperationException($"Fichier à joindre introuvable : '{resolved}'.");
        }

        /// <summary>Thread-safe cache of asynchronous lookups; a failed lookup is removed so that it is retried.</summary>
        private static async Task<TValue> GetOrAddAsync<TKey, TValue>(ConcurrentDictionary<TKey, Lazy<Task<TValue>>> cache, TKey key, Func<Task<TValue>> factory)
            where TKey : notnull
        {
            var lazy = cache.GetOrAdd(key, _ => new Lazy<Task<TValue>>(factory));

            try
            {
                return await lazy.Value;
            }
            catch
            {
                cache.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, lazy));
                throw;
            }
        }

        private sealed record ContentTypeInfo(DocSeriesContentType Type, IReadOnlyList<DocSeriesFieldDefinition> Fields)
        {
            public DocSeriesFieldDefinition? Find(string name)
                => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? Fields.FirstOrDefault(f => f.Title != null && string.Equals(f.Title, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
