using System.Collections;
using System.Globalization;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Typed reading of the values of a record returned by <see cref="IOdooClient"/>. Odoo sends <c>false</c> for an empty field:
    /// these methods return null (or an empty list) instead. A value of another type gives a <see cref="FormatException"/>.
    /// </summary>
    public static class OdooRecordExtensions
    {
        /// <summary>Text of a char / text / html / selection field, null when empty.</summary>
        public static string? GetText(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) switch
            {
                null => null,
                string text => text,
                var other => Convert.ToString(other, CultureInfo.InvariantCulture),
            };

        /// <summary>Value of a boolean field (false when absent).</summary>
        public static bool GetBool(this IReadOnlyDictionary<string, object?> record, string field)
            => record.TryGetValue(field, out var value) && value is true;

        /// <summary>Value of an integer field, or identifier of a many2one (<c>[id, "name"]</c>), null when empty.</summary>
        public static long? GetId(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) switch
            {
                null => null,
                long number => number,
                IList { Count: > 0 } reference when reference[0] is long id => id,
                var other => throw Invalid(record, field, other, "identifiant"),
            };

        /// <summary>Display name of a many2one (<c>[id, "name"]</c>), null when empty.</summary>
        public static string? GetReferenceName(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) is IList { Count: > 1 } reference ? reference[1] as string : null;

        /// <summary>Identifiers of a many2many / one2many field (empty when none).</summary>
        public static IReadOnlyList<long> GetIds(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) switch
            {
                null => [],
                IList ids => ids.Cast<object?>().Select(i => i is long id ? id : throw Invalid(record, field, i, "liste d'identifiants")).ToList(),
                var other => throw Invalid(record, field, other, "liste d'identifiants"),
            };

        /// <summary>
        /// Value of a float / monetary / integer field as a <see cref="decimal"/>, null when absent. Odoo sends <c>false</c> for 0 only
        /// when the field has no value, which is returned as null.
        /// </summary>
        public static decimal? GetDecimal(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) switch
            {
                null => null,
                long number => number,
                double number => (decimal)number,
                decimal number => number,
                var other => throw Invalid(record, field, other, "nombre"),
            };

        /// <summary>Value of a date field, null when empty.</summary>
        public static DateTime? GetDate(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) is { } value ? OdooFormats.ParseDate(value) ?? throw Invalid(record, field, value, "date") : null;

        /// <summary>Value of a datetime field in UTC (<see cref="DateTimeKind.Utc"/>), null when empty.</summary>
        public static DateTime? GetDateTimeUtc(this IReadOnlyDictionary<string, object?> record, string field)
            => Value(record, field) is { } value ? OdooFormats.ParseDateTime(value) ?? throw Invalid(record, field, value, "date et heure") : null;

        /// <summary>Value of a datetime field converted into <paramref name="timeZone"/>, null when empty.</summary>
        public static DateTime? GetDateTime(this IReadOnlyDictionary<string, object?> record, string field, TimeZoneInfo timeZone)
            => record.GetDateTimeUtc(field) is { } utc ? TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone) : null;

        /// <summary>Value of the field, null for an absent field or the <c>false</c> of an empty one.</summary>
        private static object? Value(IReadOnlyDictionary<string, object?> record, string field)
            => record.TryGetValue(field, out var value) && value is not false ? value : null;

        private static FormatException Invalid(IReadOnlyDictionary<string, object?> record, string field, object? value, string expected)
            => new($"Le champ Odoo '{field}' de l'enregistrement {record.GetValueOrDefault("id")} vaut '{value}' : ce n'est pas un(e) {expected}.");
    }
}
