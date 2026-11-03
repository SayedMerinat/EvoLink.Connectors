using System.Collections;
using System.Globalization;
using EvoLink.Connectors.Common;
using EvoLink.Connectors.Odoo.Client;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>
    /// Conversions between the values of the entities and the values of Odoo, depending on the type of the field.
    /// <list type="bullet">
    /// <item>Read: <c>false</c> of an empty field becomes null (except booleans), a many2one <c>[id, "name"]</c> becomes its id,
    /// an x2many becomes the sorted list of its ids, floats become decimals.</item>
    /// <item>Write: dates as <c>yyyy-MM-dd</c>, datetimes in UTC as <c>yyyy-MM-dd HH:mm:ss</c>, numbers through <see cref="ValueConverter"/>,
    /// null as <c>false</c>, many2many as the command <c>[(6, 0, ids)]</c>.</item>
    /// </list>
    /// A source value converted by <see cref="ToComparableAsync"/> and the same value read from Odoo by <see cref="FromOdoo"/>
    /// have the same canonical representation, so that an unchanged record is not written again.
    /// </summary>
    public static class OdooValues
    {
        public const string DateFormat = "yyyy-MM-dd";
        public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>Fields maintained by Odoo itself, never written.</summary>
        public static readonly IReadOnlySet<string> MagicFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "display_name", "create_date", "create_uid", "write_date", "write_uid", "__last_update",
        };

        /// <summary>
        /// True when the field can be written by the connector: not a magic field, not a one2many (needs commands),
        /// not a computed field without inverse (readonly and not stored).
        /// </summary>
        public static bool IsWritable(OdooFieldInfo field)
            => !MagicFields.Contains(field.Name) && field.Type != "one2many" && !(field.Readonly && !field.Store);

        /// <summary>Value read from Odoo made comparable with the source values.</summary>
        public static object? FromOdoo(object? value, string? type)
        {
            if (value is false && type != "boolean")
                return null;

            switch (type)
            {
                case "many2one":
                    return value is IList { Count: > 0 } reference ? ValueConverter.ToInt64(reference[0]) : value;

                case "many2many" or "one2many":
                    return value is IList ids ? SortedIds(ids.Cast<object?>().Select(ValueConverter.ToInt64)) : value;

                case "float" or "monetary":
                    return ValueConverter.ToDecimal(value) ?? value;

                case "integer":
                    return ValueConverter.ToInt64(value) ?? value;

                default:
                    return value;
            }
        }

        /// <summary>
        /// Value read from Odoo for a provider: <see cref="FromOdoo"/>, then the dates become <see cref="DateTime"/> (the datetimes
        /// being converted from UTC into <paramref name="timeZone"/>).
        /// </summary>
        public static object? FromOdooForEntity(object? value, string? type, TimeZoneInfo timeZone)
        {
            var normalized = FromOdoo(value, type);

            if (normalized is not string text)
                return normalized;

            if (type == "date" && DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;

            if (type == "datetime" && DateTime.TryParseExact(text, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

            return normalized;
        }

        /// <summary>
        /// Source value converted into the representation <see cref="FromOdoo"/> gives for the same Odoo value.
        /// Relations given as text are resolved by <paramref name="resolveByName"/> (null = only identifiers are accepted).
        /// </summary>
        /// <exception cref="FormatException">The value can't be converted into the type of the field.</exception>
        /// <exception cref="OdooRelationNotFoundException">No record of the relation has the given name.</exception>
        public static async Task<object?> ToComparableAsync(object? value, OdooFieldInfo field, TimeZoneInfo timeZone, Func<string, string, Task<long?>>? resolveByName)
        {
            value = ValueNormalizer.Normalize(value);

            switch (field.Type)
            {
                case "boolean":
                    if (ValueConverter.IsEmpty(value))
                        return false;
                    return ValueConverter.ToBoolean(value) ?? throw Invalid(value, field, "booléen");

                case "integer":
                    if (ValueConverter.IsEmpty(value))
                        return 0L;
                    return ValueConverter.ToInt64(value) ?? throw Invalid(value, field, "nombre entier");

                case "float" or "monetary":
                    if (ValueConverter.IsEmpty(value))
                        return 0m;
                    return Canonical(ValueConverter.ToDecimal(value) ?? throw Invalid(value, field, "nombre"));

                case "date":
                    if (ValueConverter.IsEmpty(value))
                        return null;
                    return (ValueConverter.ToDateTime(value) ?? throw Invalid(value, field, "date")).ToString(DateFormat, CultureInfo.InvariantCulture);

                case "datetime":
                    if (ValueConverter.IsEmpty(value))
                        return null;
                    return ToUtc(ValueConverter.ToDateTime(value) ?? throw Invalid(value, field, "date et heure"), timeZone).ToString(DateTimeFormat, CultureInfo.InvariantCulture);

                case "many2one":
                    if (ValueConverter.IsEmpty(value))
                        return null;
                    if (value is IList { Count: > 0 } pair)
                        value = pair[0];
                    return await ToIdAsync(value, field, resolveByName);

                case "many2many":
                    if (ValueConverter.IsEmpty(value))
                        return new List<object?>();

                    var ids = new List<long?>();
                    foreach (var item in SplitList(value))
                        ids.Add(await ToIdAsync(item, field, resolveByName));

                    return SortedIds(ids);

                case "char" or "text" or "html" or "selection":
                    return ValueConverter.IsEmpty(value) ? null : ValueConverter.ToText(value);

                default:
                    return ValueConverter.IsEmpty(value) ? null : value;
            }
        }

        /// <summary>Value of <see cref="ToComparableAsync"/> as sent to Odoo in a <c>create</c> / <c>write</c>.</summary>
        public static object? ToPayload(object? comparable, OdooFieldInfo field)
            => field.Type switch
            {
                "many2many" => new List<object?> { new List<object?> { 6L, 0L, comparable as IList ?? new List<object?>() } },
                _ => comparable ?? false,
            };

        internal static DateTime ToUtc(DateTime value, TimeZoneInfo timeZone)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value - timeZone.GetUtcOffset(value), DateTimeKind.Utc),
            };

            // Odoo stores the datetimes to the second.
            return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
        }

        private static async Task<long?> ToIdAsync(object? value, OdooFieldInfo field, Func<string, string, Task<long?>>? resolveByName)
        {
            if (ValueConverter.ToInt64(value) is { } id)
                return id > 0 ? id : throw Invalid(value, field, "identifiant");

            var name = ValueConverter.ToText(value)?.Trim();

            if (string.IsNullOrEmpty(name))
                return null;

            if (resolveByName == null || string.IsNullOrWhiteSpace(field.Relation))
                throw new FormatException($"La valeur '{name}' du champ Odoo '{field.Name}' ({field.Type}) n'est pas un identifiant : activez resolveRelationsByName pour rechercher l'enregistrement par son nom.");

            return await resolveByName(field.Relation, name)
                ?? throw new OdooRelationNotFoundException($"Aucun enregistrement '{field.Relation}' nommé '{name}' pour le champ Odoo '{field.Name}'.");
        }

        /// <summary>Items of a multi-value: list, "1;2;3" or the EvoBus notation "[1,2,3]".</summary>
        private static IReadOnlyList<object?> SplitList(object? value)
        {
            if (value is string text)
            {
                text = text.Trim();

                if (text.StartsWith('[') && text.EndsWith(']'))
                    return text[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => (object?)s.Trim('"', '\'')).ToList();
            }

            return ValueConverter.ToList(value);
        }

        private static List<object?> SortedIds(IEnumerable<long?> ids)
            => ids.Where(i => i is > 0).Select(i => i!.Value).Distinct().Order().Select(i => (object?)i).ToList();

        /// <summary>Decimal without its trailing zeros (12.50 and 12.5 compare equal).</summary>
        private static decimal Canonical(decimal value)
            => value / 1.000000000000000000000000000000000m;

        private static FormatException Invalid(object? value, OdooFieldInfo field, string expected)
            => new($"La valeur '{ValueConverter.ToText(value)}' n'est pas un(e) {expected} valide pour le champ Odoo '{field.Name}' ({field.Type}).");
    }

    /// <summary>The record named in a relation (many2one / many2many) doesn't exist in Odoo.</summary>
    public sealed class OdooRelationNotFoundException : Exception
    {
        public OdooRelationNotFoundException(string message) : base(message) { }
    }
}
