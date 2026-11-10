using System.Collections;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Conversions between .NET values and the JSON exchanged with Odoo.
    /// </summary>
    public static class OdooJson
    {
        /// <summary>
        /// .NET value sent to Odoo: null, primitives (a <see cref="float"/> is widened to <see cref="double"/>), <see cref="DateTime"/>
        /// (date alone when it has no time, see <see cref="OdooFormats"/>), <see cref="DateTimeOffset"/> (in UTC), lists, dictionaries, JSON.
        /// </summary>
        public static JToken ToJson(object? value)
            => value switch
            {
                null => JValue.CreateNull(),
                JToken token => token,
                string text => new JValue(text),
                float number => new JValue((double)(decimal)number),
                DateTime date => new JValue(date.TimeOfDay == TimeSpan.Zero ? OdooFormats.Date(date) : OdooFormats.DateTime(date)),
                DateTimeOffset date => new JValue(OdooFormats.DateTime(date)),
                IEnumerable<KeyValuePair<string, object?>> dictionary => new JObject(dictionary.Select(p => new JProperty(p.Key, ToJson(p.Value)))),
                IEnumerable items => new JArray(items.Cast<object?>().Select(ToJson)),
                _ => JToken.FromObject(value),
            };

        /// <summary>
        /// JSON received from Odoo as plain .NET values: integers as <see cref="long"/>, floats as <see cref="double"/>, arrays as
        /// <c>List&lt;object?&gt;</c>, objects as dictionaries (case-insensitive keys), strings unchanged (the dates are not parsed).
        /// </summary>
        public static object? ToClr(JToken? token)
            => token switch
            {
                null => null,
                JValue { Type: JTokenType.Null or JTokenType.Undefined } => null,
                JValue { Type: JTokenType.Integer } value => value.Value is System.Numerics.BigInteger big ? (object)(decimal)big : Convert.ToInt64(value.Value, CultureInfo.InvariantCulture),
                JValue { Type: JTokenType.Float } value => Convert.ToDouble(value.Value, CultureInfo.InvariantCulture),
                JValue value => value.Value,
                JArray array => array.Select(ToClr).ToList(),
                JObject obj => ToRecord(obj),
                _ => token.ToString(Formatting.None),
            };

        /// <summary>An Odoo record (JSON object) as a dictionary of .NET values.</summary>
        public static Dictionary<string, object?> ToRecord(JObject record)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in record.Properties())
                result[property.Name] = ToClr(property.Value);

            return result;
        }
    }

    /// <summary>
    /// Formats of the dates expected by Odoo. The datetimes are stored in UTC, to the second.
    /// </summary>
    public static class OdooFormats
    {
        public const string DateFormat = "yyyy-MM-dd";
        public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        public static string Date(DateTime date)
            => date.ToString(DateFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Datetime for Odoo: a <see cref="DateTimeKind.Local"/> value is converted to UTC, an unspecified one is taken as UTC
        /// (use <see cref="DateTime(System.DateTime, TimeZoneInfo)"/> to give its time zone).
        /// </summary>
        public static string DateTime(DateTime value)
            => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString(DateTimeFormat, CultureInfo.InvariantCulture);

        /// <summary>Datetime for Odoo of a value expressed in <paramref name="timeZone"/> when it has no kind.</summary>
        public static string DateTime(DateTime value, TimeZoneInfo timeZone)
            => DateTime(ToUtc(value, timeZone));

        public static string DateTime(DateTimeOffset value)
            => value.UtcDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

        /// <summary>UTC value, truncated to the second like Odoo stores it.</summary>
        public static DateTime ToUtc(DateTime value, TimeZoneInfo timeZone)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => System.DateTime.SpecifyKind(value - timeZone.GetUtcOffset(value), DateTimeKind.Utc),
            };

            return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
        }

        /// <summary>Date sent by Odoo (yyyy-MM-dd), null when empty (false).</summary>
        public static DateTime? ParseDate(object? value)
            => value is string text && System.DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        /// <summary>Datetime sent by Odoo (UTC), as a <see cref="DateTimeKind.Utc"/> value; null when empty (false).</summary>
        public static DateTime? ParseDateTime(object? value)
            => value is string text && System.DateTime.TryParseExact(text, DateTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
                ? System.DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : null;
    }

    /// <summary>
    /// Commands of the relational fields one2many / many2many in a <c>create</c> or a <c>write</c>.
    /// </summary>
    public static class OdooCommands
    {
        /// <summary>Replaces the linked records by <paramref name="ids"/>: <c>[(6, 0, ids)]</c>.</summary>
        public static List<object?> Set(IEnumerable<long> ids)
            => [new List<object?> { 6L, 0L, ids.Select(i => (object?)i).ToList() }];

        /// <summary>Adds links to existing records: <c>(4, id)</c> for each.</summary>
        public static List<object?> Link(IEnumerable<long> ids)
            => ids.Select(i => (object?)new List<object?> { 4L, i }).ToList();

        /// <summary>Removes links without deleting the records: <c>(3, id)</c> for each.</summary>
        public static List<object?> Unlink(IEnumerable<long> ids)
            => ids.Select(i => (object?)new List<object?> { 3L, i }).ToList();

        /// <summary>Removes every link: <c>[(5,)]</c>.</summary>
        public static List<object?> Clear()
            => [new List<object?> { 5L }];

        /// <summary>Creates linked records (one2many lines): <c>(0, 0, values)</c> for each.</summary>
        public static List<object?> Create(IEnumerable<IReadOnlyDictionary<string, object?>> records)
            => records.Select(r => (object?)new List<object?> { 0L, 0L, r }).ToList();
    }
}
