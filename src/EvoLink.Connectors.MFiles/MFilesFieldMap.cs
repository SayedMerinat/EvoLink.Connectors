using EvoLink.Connectors.Common;
using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.MFiles
{
    /// <summary>
    /// Keys shared with the other connectors (same names as EvoBus to ease the migration) and metadata set on the entities.
    /// </summary>
    public static class MFilesKeys
    {
        public const string FilePrefix = "FILE-";
        public const string FilePath = "FILE-FilePath";
        public const string CreatedBy = "FILE-CreatedBy";
        public const string CreationTimeStamp = "FILE-CreationTimeStamp";

        /// <summary>Corrected key: in EvoBus the constant pointed to "FILE-CreatedBy".</summary>
        public const string LastModifiedBy = "FILE-LastModifiedBy";

        public const string LastModificationTimeStamp = "FILE-LastModificationTimeStamp";

        public const string MetadataObjectId = "mfiles.objectId";
        public const string MetadataObjectType = "mfiles.objectType";
        public const string MetadataVersion = "mfiles.version";
        public const string MetadataClassId = "mfiles.classId";

        /// <summary>Built-in "Class" property (written by the gateway from the configured class).</summary>
        public const int ClassPropertyDef = 100;

        /// <summary>
        /// Built-in properties maintained by M-Files (created, last modified, single file, sizes, deleted...): never written as
        /// properties, the creation / modification info being set by the audit options.
        /// </summary>
        public static readonly IReadOnlySet<int> ReadOnlyPropertyDefs = new HashSet<int> { 20, 21, 22, 23, 24, 25, 27, 28, 30, 31, 40, 81, 89, 90, 91, 92, 93, 94, 101 };
    }

    /// <summary>
    /// Correspondence between the entity properties and the M-Files property definitions, built from the <c>fields</c> mapping
    /// (entity property → M-Files property name / alias / ID) through <see cref="FieldMapping"/>.
    /// </summary>
    internal sealed class MFilesFieldMap
    {
        private readonly MFilesSession _session;
        private readonly Dictionary<int, string> _propertyById = [];
        private readonly HashSet<string> _mappedProperties = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int>? _selection;

        private MFilesFieldMap(MFilesSession session, FieldMapping mapping, HashSet<int>? selection)
        {
            _session = session;
            Mapping = mapping;
            _selection = selection;
        }

        public FieldMapping Mapping { get; }

        /// <summary>
        /// Resolves the configured fields. Unknown M-Files properties in the configuration are configuration errors.
        /// </summary>
        /// <param name="readProperties">M-Files properties read (empty: the mapped fields with <c>onlyMappedFields</c>, otherwise all).</param>
        public static MFilesFieldMap Build(string module, MFilesSession session, IReadOnlyDictionary<string, string>? fields, bool onlyMappedFields,
            IReadOnlyList<string>? readProperties = null)
        {
            var mapping = new FieldMapping(fields, onlyMappedFields);
            var resolved = new List<(string Property, MFilesPropertyDefInfo Definition)>();

            foreach (var (property, field) in fields ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(property) || string.IsNullOrWhiteSpace(field))
                    continue;

                resolved.Add((property.Trim(), RequireProperty(module, session, field, $"fields.{property}")));
            }

            HashSet<int>? selection = null;

            if (readProperties is { Count: > 0 })
                selection = readProperties.Select(p => RequireProperty(module, session, p, "properties").Id).ToHashSet();
            else if (mapping.OnlyMappedFields)
                selection = resolved.Select(r => r.Definition.Id).ToHashSet();

            var map = new MFilesFieldMap(session, mapping, selection);

            foreach (var (property, definition) in resolved)
            {
                map._propertyById[definition.Id] = property;
                map._mappedProperties.Add(property);
            }

            return map;
        }

        public static MFilesPropertyDefInfo RequireProperty(string module, MFilesSession session, string identifier, string what)
            => session.FindProperty(identifier)
               ?? throw ConnectorGuard.Invalid(module, $"Propriété M-Files '{identifier}' ({what}) introuvable dans le coffre {session.VaultName}.");

        public bool IsMapped(string property)
            => _mappedProperties.Contains(property);

        /// <summary>Entity property receiving an M-Files property.</summary>
        public string PropertyName(int propertyDefId)
            => _propertyById.TryGetValue(propertyDefId, out var mapped) ? mapped : _session.FindProperty(propertyDefId)?.Name ?? $"PD{propertyDefId}";

        /// <summary>
        /// Builds an entity from an M-Files object: the selected (or all) properties, renamed through the mapping. The selected
        /// properties missing on the object are set to null so that they can be compared.
        /// </summary>
        public Entity ToEntity(MFilesObject obj, bool lookupsAsIds, string? objectIdProperty)
        {
            var entity = new Entity();
            var wanted = _selection ?? (IEnumerable<int>)obj.Properties.Keys.Concat(_propertyById.Keys).Distinct();

            foreach (var id in wanted)
                entity[PropertyName(id)] = MFilesSession.ToEntityValue(obj.Properties.GetValueOrDefault(id), lookupsAsIds);

            if (!string.IsNullOrWhiteSpace(objectIdProperty))
                entity[objectIdProperty] = (long)obj.Ref.Id;

            SetMetadata(entity, obj);
            return entity;
        }

        public static void SetMetadata(Entity entity, MFilesObject obj)
        {
            entity.Metadata[MFilesKeys.MetadataObjectId] = obj.Ref.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            entity.Metadata[MFilesKeys.MetadataObjectType] = obj.Ref.ObjectType.ToString(System.Globalization.CultureInfo.InvariantCulture);
            entity.Metadata[MFilesKeys.MetadataVersion] = obj.Ref.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            entity.Metadata[MFilesKeys.MetadataClassId] = obj.ClassId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Simple search conditions (property = value) of the configuration, the values being converted into the type of the
        /// property (lookups resolved by name or ID). An unknown property or item is a configuration error.
        /// </summary>
        public static List<MFilesPropertyValue> BuildConditions(string module, MFilesSession session, IReadOnlyDictionary<string, string?>? conditions)
        {
            var result = new List<MFilesPropertyValue>();

            foreach (var (identifier, value) in conditions ?? new Dictionary<string, string?>())
            {
                var definition = RequireProperty(module, session, identifier, "conditions");

                try
                {
                    result.Add(session.ToPropertyValue(definition, value));
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    throw ConnectorGuard.Invalid(module, $"Condition '{identifier}' = '{value}' invalide : {ex.Message}");
                }
            }

            return result;
        }
    }
}
