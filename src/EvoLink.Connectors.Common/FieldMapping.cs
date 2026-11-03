using Ortolan.Core.Models;

namespace EvoLink.Connectors.Common
{
    /// <summary>
    /// Correspondence between the properties of the entities and the fields of a system (columns, M-Files properties,
    /// Odoo fields...). Every connector exposes it through a <c>fields</c> property (entity property → field).
    /// <list type="bullet">
    /// <item>Empty mapping: every property of the entity is used with the same name.</item>
    /// <item>With <see cref="OnlyMappedFields"/>: only the mapped properties are written.</item>
    /// </list>
    /// </summary>
    public sealed class FieldMapping
    {
        private readonly Dictionary<string, string> _toField;
        private readonly Dictionary<string, string> _toProperty;

        public bool OnlyMappedFields { get; }

        public FieldMapping(IReadOnlyDictionary<string, string>? fields, bool onlyMappedFields = false)
        {
            _toField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _toProperty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (property, field) in fields ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(property) || string.IsNullOrWhiteSpace(field))
                    continue;

                _toField[property.Trim()] = field.Trim();
                _toProperty[field.Trim()] = property.Trim();
            }

            OnlyMappedFields = onlyMappedFields && _toField.Count > 0;
        }

        public bool IsEmpty => _toField.Count == 0;

        /// <summary>True when the property of the entity has an explicit correspondence.</summary>
        public bool IsMapped(string property)
            => _toField.ContainsKey(property);

        /// <summary>Name of the field of the system for a property of the entity.</summary>
        public string ToField(string property)
            => _toField.TryGetValue(property, out var field) ? field : property;

        /// <summary>Name of the property of the entity for a field of the system.</summary>
        public string ToProperty(string field)
            => _toProperty.TryGetValue(field, out var property) ? property : field;

        /// <summary>
        /// Fields and values to write for an entity, limited to <paramref name="properties"/> when given (synchronised fields).
        /// </summary>
        public IReadOnlyList<(string Field, object? Value)> ToFields(Entity entity, IEnumerable<string>? properties = null)
        {
            var selected = properties?.ToList() is { Count: > 0 } list ? list : entity.Properties.Keys.ToList();
            var result = new List<(string, object?)>();

            foreach (var property in selected.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (OnlyMappedFields && !_toField.ContainsKey(property))
                    continue;

                if (entity.TryGetValue(property, out var value))
                    result.Add((ToField(property), value));
            }

            return result;
        }

        /// <summary>
        /// Builds an entity from a record of the system, the fields being renamed into entity properties.
        /// </summary>
        public Entity ToEntity(IEnumerable<KeyValuePair<string, object?>> record)
        {
            var entity = new Entity();

            foreach (var (field, value) in record)
            {
                if (OnlyMappedFields && !_toProperty.ContainsKey(field))
                    continue;

                entity[ToProperty(field)] = value;
            }

            return entity;
        }
    }
}
