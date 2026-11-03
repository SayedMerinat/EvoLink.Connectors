using System.ComponentModel;
using EvoLink.Connectors.Common;
using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;
using Ortolan.Core.Sync;

namespace EvoLink.Connectors.MFiles
{
    /// <summary>
    /// Synchronises the entities with the objects of an M-Files class: creation (with an optional attached file), update of the
    /// changed properties only (check-out / set properties / check-in) and deletion (marked as deleted, or destroyed).
    /// <para>
    /// The target is read through the key fields (<c>keyFields</c> of the service or of the module) mapped on M-Files
    /// properties. The values are converted into the M-Files types, the lookups and multi-select lookups being resolved by
    /// name, display ID or ID.
    /// </para>
    /// </summary>
    [OrtolanType("mfiles-synchroniser")]
    [Description("Synchronise les entités avec les objets d'une classe M-Files (création avec fichier joint, mise à jour des propriétés modifiées, suppression).")]
    public class MFilesSynchroniser : SynchroniserModule, IMFilesConnectionOptions
    {
        private readonly Dictionary<Entity, MFilesObject> _objects = new(ReferenceEqualityComparer.Instance);
        private MFilesSession? _session;
        private MFilesClassInfo? _class;
        private MFilesFieldMap? _map;
        private List<MFilesPropertyValue> _conditions = [];

        [Description("Adresse du serveur M-Files.")]
        public string Server { get; set; } = "localhost";

        [Description("Protocole de connexion (ncacn_ip_tcp, ncacn_http, ncacn_spnego, grpc...).")]
        public string Protocol { get; set; } = MFilesConnectionSettings.DefaultProtocol;

        [Description("Port du serveur M-Files.")]
        public int Port { get; set; } = MFilesConnectionSettings.DefaultPort;

        [Description("Nom ou GUID du coffre.")]
        public string Vault { get; set; } = string.Empty;

        [Description("Authentification : CurrentWindowsUser (compte du service), SpecificWindowsUser ou MFilesUser.")]
        public MFilesAuthType AuthType { get; set; } = MFilesAuthType.CurrentWindowsUser;

        [Description("Utilisateur (authentification SpecificWindowsUser ou MFilesUser).")]
        public string? Username { get; set; }

        [Sensitive]
        [Description("Mot de passe de l'utilisateur.")]
        public string? Password { get; set; }

        [Description("Domaine Windows (authentification SpecificWindowsUser).")]
        public string? Domain { get; set; }

        [Description("Classe des objets synchronisés : nom, alias ou ID (le type d'objet est celui de la classe).")]
        public string Class { get; set; } = string.Empty;

        [Description("Conditions limitant les objets de la cible (propriété = valeur), utiles en mode miroir.")]
        public Dictionary<string, string?> Conditions { get; set; } = [];

        [Description("Correspondance des champs : propriété de l'entité → propriété M-Files (nom, alias ou ID). Vide = mêmes noms.")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("N'écrit et ne lit que les propriétés présentes dans 'fields'.")]
        public bool OnlyMappedFields { get; set; }

        [Description("Ignore les propriétés de l'entité inconnues dans le coffre (sinon l'entité passe en échec).")]
        public bool IgnoreUnknownProperties { get; set; }

        [Description("Crée les éléments de listes de valeurs manquants (impossible pour les listes d'objets).")]
        public bool CreateMissingValueListItems { get; set; }

        [Description("Lit les listes de valeurs de la cible sous forme d'ID plutôt que de noms (comparaison avec des sources qui fournissent des ID).")]
        public bool LookupsAsIds { get; set; }

        [Description("Propriété de l'entité recevant l'ID de l'objet M-Files (vide = aucune ; l'ID est toujours dans les métadonnées 'mfiles.objectId').")]
        public string? ObjectIdProperty { get; set; }

        [Description("Détruit les objets supprimés au lieu de les marquer comme supprimés.")]
        public bool DestroyOnDelete { get; set; }

        [Description("Joint à l'objet créé le fichier indiqué par 'filePathProperty' (document à fichier unique).")]
        public bool AttachFile { get; set; } = true;

        [Description("Propriété de l'entité contenant le chemin du fichier à joindre.")]
        public string FilePathProperty { get; set; } = MFilesKeys.FilePath;

        [Description("Applique le créateur, le dernier modificateur et leurs dates à partir des propriétés de l'entité (opérations administratives).")]
        public bool SetAuditInfo { get; set; }

        [Description("Propriété de l'entité contenant le login du créateur.")]
        public string CreatedByProperty { get; set; } = MFilesKeys.CreatedBy;

        [Description("Propriété de l'entité contenant la date de création.")]
        public string CreatedProperty { get; set; } = MFilesKeys.CreationTimeStamp;

        [Description("Propriété de l'entité contenant le login du dernier modificateur.")]
        public string LastModifiedByProperty { get; set; } = MFilesKeys.LastModifiedBy;

        [Description("Propriété de l'entité contenant la date de dernière modification.")]
        public string LastModifiedProperty { get; set; } = MFilesKeys.LastModificationTimeStamp;

        [Description("ID de l'utilisateur M-Files utilisé lorsqu'un login est introuvable (vide = l'utilisateur n'est pas modifié).")]
        public int? DefaultUserId { get; set; }

        private MFilesSession Session => _session ?? throw new InvalidOperationException("La connexion au coffre M-Files n'est pas ouverte.");

        /// <summary>Creates the gateway to the vault (overridden by the tests).</summary>
        protected virtual IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
            => new ComMFilesGateway(settings);

        protected override Task OpenAsync(JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("class", Class));

            if (SetAuditInfo && DefaultUserId is <= 0)
                throw ConnectorGuard.Invalid(DisplayName, "'defaultUserId' doit être un ID d'utilisateur M-Files positif.");

            _objects.Clear();
            _session = MFilesConnector.Open(DisplayName, this, CreateGateway, Unavailable, context);

            Call(() =>
            {
                _class = _session.FindClass(Class)
                    ?? throw ConnectorGuard.Invalid(DisplayName, $"Classe M-Files '{Class}' introuvable dans le coffre {_session.VaultName}.");
                _map = MFilesFieldMap.Build(DisplayName, _session, Fields, OnlyMappedFields);
                _conditions = MFilesFieldMap.BuildConditions(DisplayName, _session, Conditions);
            });

            return Task.CompletedTask;
        }

        protected override Task<IEnumerable<Entity>> ReadTargetAsync(JobContext context)
        {
            var objects = Call(() => Session.Gateway.Search(new MFilesSearch(_class!.Id, _conditions)));
            var entities = new List<Entity>(objects.Count);

            foreach (var obj in objects)
            {
                var entity = _map!.ToEntity(obj, LookupsAsIds, ObjectIdProperty);
                _objects[entity] = obj;
                entities.Add(entity);
            }

            return Task.FromResult<IEnumerable<Entity>>(entities);
        }

        protected override Task<Entity> CreateAsync(Entity source, JobContext context)
        {
            var values = BuildValues(source, null, context).Where(v => v.Value != null).ToList();
            var file = GetFile(source, context);

            if (file != null)
                values = ApplyFileName(values, file);

            var audit = BuildAudit(source, creation: true, context);
            var created = Call(() => Session.Gateway.Create(new MFilesCreateRequest(_class!.ObjectType, _class.Id, values, file, audit)));

            Complete(source, created);
            return Task.FromResult(source);
        }

        protected override Task<Entity> UpdateAsync(Entity source, Entity existing, JobContext context)
        {
            var current = _objects[existing];
            var syncFields = SyncFields ?? context.Sync.SyncFields;
            var keyFields = KeyFields is { Count: > 0 } ? KeyFields : context.Sync.KeyFields;
            var fields = syncFields.Count > 0 ? keyFields.Concat(syncFields).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;

            var changed = BuildValues(source, fields, context)
                .Where(v => !MFilesSession.SameValue(current.Properties.GetValueOrDefault(v.PropertyDefId), v))
                .ToList();

            if (changed.Count == 0)
            {
                // The source differs only by the representation of its values (e.g. "1.50" and 1.5): nothing to write.
                context.LogDebug($"[{DisplayName}] Objet {current.Ref.Id} : aucune propriété M-Files modifiée.");
                Complete(source, current);
                return Task.FromResult(source);
            }

            var audit = BuildAudit(source, creation: false, context);
            var updated = Call(() => Session.Gateway.Update(current.Ref, changed, audit));

            Complete(source, updated);
            return Task.FromResult(source);
        }

        protected override Task DeleteAsync(Entity existing, JobContext context)
        {
            var current = _objects[existing];
            Call(() => Session.Gateway.Delete(current.Ref, DestroyOnDelete));
            return Task.CompletedTask;
        }

        protected override Task CloseAsync(JobContext context)
        {
            _objects.Clear();
            _session?.Close(context, DisplayName);
            _session = null;
            return Task.CompletedTask;
        }

        // ---------------------------------------------------------------- values

        /// <summary>
        /// Typed values of the entity properties (all of them, or <paramref name="properties"/>), through the field mapping.
        /// Skipped: the file keys (FILE-*) that are not mapped, the object ID property, the class and the properties maintained by M-Files.
        /// </summary>
        private List<MFilesPropertyValue> BuildValues(Entity source, IReadOnlyList<string>? properties, JobContext context)
        {
            var map = _map!;
            var values = new Dictionary<int, MFilesPropertyValue>();

            foreach (var property in (properties ?? source.Properties.Keys.ToList()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!source.TryGetValue(property, out var value) || IsSkipped(property, map))
                    continue;

                var field = map.Mapping.ToField(property);
                var definition = Session.FindProperty(field);

                if (definition == null)
                {
                    if (IgnoreUnknownProperties)
                        continue;

                    throw new InvalidOperationException($"Propriété M-Files '{field}' introuvable (propriété '{property}' de l'entité). " +
                        "Utilisez 'fields', 'onlyMappedFields' ou 'ignoreUnknownProperties'.");
                }

                if (definition.Id == MFilesKeys.ClassPropertyDef || definition.IsAutomatic || MFilesKeys.ReadOnlyPropertyDefs.Contains(definition.Id))
                {
                    context.LogDebug($"[{DisplayName}] Propriété '{definition.Name}' gérée par M-Files, non écrite.");
                    continue;
                }

                values[definition.Id] = Session.ToPropertyValue(definition, value, CreateMissingValueListItems);
            }

            return [.. values.Values];
        }

        private bool IsSkipped(string property, MFilesFieldMap map)
        {
            if (map.Mapping.OnlyMappedFields && !map.IsMapped(property))
                return true;

            if (!string.IsNullOrWhiteSpace(ObjectIdProperty) && string.Equals(property, ObjectIdProperty, StringComparison.OrdinalIgnoreCase))
                return true;

            return !map.IsMapped(property) && property.StartsWith(MFilesKeys.FilePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private MFilesFile? GetFile(Entity source, JobContext context)
        {
            if (!AttachFile || string.IsNullOrWhiteSpace(FilePathProperty) || ValueConverter.ToText(source.GetValueOrDefault(FilePathProperty)) is not { Length: > 0 } text)
                return null;

            var path = context.ResolvePath(text.Trim());

            // Not a FileNotFoundException: a missing source file is an error of the entity, not an unavailability of the target.
            if (!File.Exists(path))
                throw new InvalidOperationException($"Le fichier à joindre '{path}' est introuvable.");

            return new MFilesFile(path, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path).TrimStart('.'));
        }

        /// <summary>The name of a single-file document defaults to the file name, without repeating its extension.</summary>
        private List<MFilesPropertyValue> ApplyFileName(List<MFilesPropertyValue> values, MFilesFile file)
        {
            var nameId = _class!.NamePropertyDefId;
            var name = values.FirstOrDefault(v => v.PropertyDefId == nameId);
            var title = name?.Value as string;

            if (string.IsNullOrWhiteSpace(title))
                title = file.Title;
            else if (file.Extension.Length > 0 && title.EndsWith("." + file.Extension, StringComparison.OrdinalIgnoreCase))
                title = title[..^(file.Extension.Length + 1)];
            else
                return values;

            return [.. values.Where(v => v.PropertyDefId != nameId), new MFilesPropertyValue(nameId, MFilesDataType.Text, title)];
        }

        private MFilesAuditInfo? BuildAudit(Entity source, bool creation, JobContext context)
        {
            if (!SetAuditInfo)
                return null;

            var audit = new MFilesAuditInfo(
                creation ? ResolveUser(source, CreatedByProperty, context) : null,
                creation ? ResolveUtc(source, CreatedProperty) : null,
                ResolveUser(source, LastModifiedByProperty, context),
                ResolveUtc(source, LastModifiedProperty));

            return audit.IsEmpty ? null : audit;
        }

        private int? ResolveUser(Entity source, string property, JobContext context)
        {
            var login = ValueConverter.ToText(source.GetValueOrDefault(property))?.Trim();
            if (string.IsNullOrEmpty(login))
                return null;

            if (Call(() => Session.FindUser(login)) is { } user)
                return user.Id;

            if (DefaultUserId != null)
                return DefaultUserId;

            context.LogWarning($"[{DisplayName}] Utilisateur M-Files '{login}' ({property}) introuvable : l'utilisateur n'est pas modifié.");
            return null;
        }

        private static DateTime? ResolveUtc(Entity source, string property)
        {
            var value = source.GetValueOrDefault(property);
            if (ValueConverter.IsEmpty(value))
                return null;

            var date = ValueConverter.ToDateTime(value)
                ?? throw new FormatException($"Date '{ValueConverter.ToText(value)}' ({property}) invalide.");

            // A date without time zone is a local time of the machine (file dates, Swiss formats).
            return date.Kind == DateTimeKind.Utc ? date : DateTime.SpecifyKind(date, DateTimeKind.Local).ToUniversalTime();
        }

        private void Complete(Entity source, MFilesObject obj)
        {
            MFilesFieldMap.SetMetadata(source, obj);

            if (!string.IsNullOrWhiteSpace(ObjectIdProperty))
                source[ObjectIdProperty] = (long)obj.Ref.Id;
        }

        // ---------------------------------------------------------------- failures

        private T Call<T>(Func<T> call)
            => MFilesConnector.Call(call, Unavailable);

        private void Call(Action call)
            => MFilesConnector.Call(call, Unavailable);

        private Exception Unavailable(Exception ex)
            => ConnectorGuard.TargetUnavailable(DisplayName, $"M-Files {MFilesConnectionSettings.From(this).Display}", ex);
    }
}
