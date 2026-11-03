using System.Collections;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Common
{
    /// <summary>
    /// Tolerant conversions of the entity values (strings read from CSV, numbers from JSON, dates in Swiss formats...)
    /// into the types expected by the targets. Every method is culture-invariant and never throws: it returns null when the
    /// value can't be converted, the caller decides whether that is an error.
    /// </summary>
    public static class ValueConverter
    {
        private static readonly string[] DateFormats =
        [
            "o", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "d.M.yyyy",
            "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "d/M/yyyy",
            "yyyyMMdd", "yyyyMMddHHmmss",
        ];

        private static readonly HashSet<string> TrueValues = new(StringComparer.OrdinalIgnoreCase) { "true", "1", "yes", "oui", "y", "o", "vrai", "x" };
        private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase) { "false", "0", "no", "non", "n", "faux", "" };

        public static bool IsEmpty(object? value)
            => value switch
            {
                null => true,
                string s => string.IsNullOrWhiteSpace(s),
                JValue { Type: JTokenType.Null } => true,
                ICollection collection => collection.Count == 0,
                _ => false,
            };

        public static string? ToText(object? value)
            => value switch
            {
                null => null,
                string s => s,
                DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                JToken token => ToText(ValueNormalizer.Normalize(token)),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                IEnumerable enumerable => string.Join(";", enumerable.Cast<object?>().Select(ToText)),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };

        public static long? ToInt64(object? value)
        {
            switch (value)
            {
                case null: return null;
                case long l: return l;
                case int i: return i;
                case short s: return s;
                case byte b: return b;
                case bool b: return b ? 1 : 0;
                case decimal or double or float:
                    var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return d == Math.Truncate(d) ? (long)d : null;
            }

            var text = ToText(value)?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            return ToDecimal(text) is { } dec && dec == Math.Truncate(dec) ? (long)dec : null;
        }

        public static decimal? ToDecimal(object? value)
        {
            switch (value)
            {
                case null: return null;
                case decimal d: return d;
                case long or int or short or byte: return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                case double or float:
                    var dbl = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return double.IsFinite(dbl) && Math.Abs(dbl) < 7.9e28 ? Convert.ToDecimal(dbl) : null;
            }

            var text = ToText(value)?.Trim().Replace("'", string.Empty).Replace("’", string.Empty).Replace(" ", string.Empty);
            if (string.IsNullOrEmpty(text))
                return null;

            // "1234,50" (decimal comma) and "1,234.50" (thousands comma) are both accepted.
            if (text.Contains(',') && !text.Contains('.'))
                text = text.Replace(',', '.');
            else if (text.Contains(',') && text.Contains('.'))
                text = text.Replace(",", string.Empty);

            return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        public static bool? ToBoolean(object? value)
        {
            switch (value)
            {
                case null: return null;
                case bool b: return b;
                case long or int or short or byte: return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
            }

            var text = ToText(value)?.Trim() ?? string.Empty;

            if (TrueValues.Contains(text))
                return true;

            return FalseValues.Contains(text) ? false : null;
        }

        public static DateTime? ToDateTime(object? value)
        {
            switch (value)
            {
                case null: return null;
                case DateTime dt: return dt;
                case DateTimeOffset dto: return dto.LocalDateTime;
            }

            var text = ToText(value)?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
                return exact;

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
        }

        /// <summary>
        /// Splits a multi-value (list, JSON array, "a;b;c" text) into its items.
        /// </summary>
        public static IReadOnlyList<object?> ToList(object? value, char separator = ';')
            => value switch
            {
                null => [],
                string s => s.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Cast<object?>().ToList(),
                JArray array => array.Select(ValueNormalizer.Normalize).ToList(),
                IEnumerable enumerable and not IDictionary => enumerable.Cast<object?>().ToList(),
                _ => [value],
            };
    }
}
