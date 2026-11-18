using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EvoLink.Connectors.Common;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>How the relations (many2one, many2many, one2many) are given to and taken from the entities.</summary>
    public enum OdooRelationFormat
    {
        /// <summary>many2one = identifier, x2many = list of identifiers (default).</summary>
        Id = 0,

        /// <summary>
        /// Text format of the OdooToolbox tools (odootosql / sqltoodoo), Python representation of the value read from Odoo:
        /// many2one <c>[5, 'Nom']</c>, x2many <c>[1, 2]</c>, empty relation = null.
        /// </summary>
        OdooToolbox = 1,
    }

    /// <summary>How the values other than the relations are given to the entities by <see cref="OdooProvider"/>.</summary>
    public enum OdooValueFormat
    {
        /// <summary>Values of Odoo: numbers, texts, dates, lists and dictionaries as read (default).</summary>
        Native = 0,

        /// <summary>
        /// Values as odootosql wrote them: lists and dictionaries (<c>json</c>, <c>properties</c>...) as their Python
        /// representation (<c>{'body': [{'patch': '...', 'revision_id': 1}]}</c>), and the empty values of Python (<c>0</c>,
        /// <c>0.0</c>, <c>''</c>, <c>[]</c>, <c>{}</c>) as null. Booleans are kept.
        /// </summary>
        OdooToolbox = 1,
    }

    /// <summary>
    /// Reading and writing of the relations in the text format of OdooToolbox, so that the SQL tables it produced (and the views
    /// or scripts parsing them) keep working.
    /// </summary>
    public static partial class OdooToolboxFormat
    {
        public static bool IsRelation(string? type)
            => type is "many2one" or "many2many" or "one2many";

        /// <summary>
        /// Relation read from Odoo as OdooToolbox wrote it: <c>[5, 'Nom']</c>, <c>[1, 2]</c> (order of Odoo), null when empty.
        /// </summary>
        public static string? FromOdoo(object? value, string? type)
            => value is IList { Count: > 0 } list ? Repr(list) : null;

        /// <summary>
        /// Value other than a relation as odootosql wrote it (<see cref="OdooValueFormat.OdooToolbox"/>), from the value already
        /// converted for the entity: lists and dictionaries become their Python representation, the empty values of Python null.
        /// </summary>
        public static object? ValueFromOdoo(object? value, string? type)
            => value switch
            {
                bool when type == "boolean" => value,
                null or "" or false => null,
                long or int or short or byte when Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0 => null,
                decimal number when number == 0 => null,
                double number when number == 0 => null,
                IDictionary { Count: 0 } or IList { Count: 0 } => null,
                IDictionary dictionary => ReprDictionary(dictionary),
                IList list => Repr(list),
                _ => value,
            };

        /// <summary>
        /// Identifier of a many2one given as OdooToolbox reads it: <c>[5, 'Nom']</c>, <c>[5]</c> or <c>(5,)</c>. Null when the text is
        /// not in that format (the value is then converted as usual).
        /// </summary>
        public static long? ParseMany2One(object? value)
        {
            if (value is not string text)
                return null;

            var match = Many2OnePattern().Match(text);
            return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
        }

        /// <summary>
        /// Identifiers of a many2many given as OdooToolbox reads it: <c>[1, 2]</c>, <c>(1, 2)</c> or <c>1,2</c>. Null when the text is
        /// not a list of integers.
        /// </summary>
        public static List<object?>? ParseIds(object? value)
        {
            if (value is not string text)
                return null;

            text = text.Trim();
            if (text.Length >= 2 && ((text[0] == '[' && text[^1] == ']') || (text[0] == '(' && text[^1] == ')')))
                text = text[1..^1];

            var ids = new List<object?>();
            foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    return null;

                ids.Add(id);
            }

            return ids;
        }

        /// <summary>Python representation (<c>repr</c>) of a list of numbers and texts, as written by OdooToolbox.</summary>
        internal static string Repr(IList list)
            => "[" + string.Join(", ", list.Cast<object?>().Select(ReprItem)) + "]";

        /// <summary>Python representation of a dictionary (JSON object of Odoo), its keys in the order of Odoo.</summary>
        internal static string ReprDictionary(IDictionary dictionary)
        {
            var items = new List<string>(dictionary.Count);

            foreach (DictionaryEntry entry in dictionary)
                items.Add(ReprItem(entry.Key) + ": " + ReprItem(entry.Value));

            return "{" + string.Join(", ", items) + "}";
        }

        private static string ReprItem(object? item)
            => item switch
            {
                null => "None",
                bool b => b ? "True" : "False",
                string s => ReprString(s),
                long or int or short or byte => Convert.ToString(item, CultureInfo.InvariantCulture)!,
                double d => ReprFloat(d),
                float f => ReprFloat(f),
                decimal => ValueConverter.ToText(item)!,
                IDictionary dictionary => ReprDictionary(dictionary),
                IList inner => Repr(inner),
                _ => ReprString(ValueConverter.ToText(item) ?? string.Empty),
            };

        /// <summary>
        /// Python 3 <c>repr</c> of a float: the shortest text giving back the same number, <c>.0</c> for an integral value
        /// (<c>1.0</c>), the exponent form below 1e-4 and from 1e16 (<c>1e-05</c>, <c>1.5e+16</c>).
        /// </summary>
        internal static string ReprFloat(double value)
        {
            if (double.IsNaN(value))
                return "nan";

            if (double.IsInfinity(value))
                return value > 0 ? "inf" : "-inf";

            var sign = double.IsNegative(value) ? "-" : string.Empty;

            if (value == 0)
                return sign + "0.0";

            // Shortest round-trip text of .NET ("123.45", "0.0001", "1.5E-05"), as digits and position of the point:
            // value = 0.digits × 10^point.
            var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
            var exponentIndex = shortest.IndexOf('E');
            var mantissa = exponentIndex < 0 ? shortest : shortest[..exponentIndex];
            var exponent = exponentIndex < 0 ? 0 : int.Parse(shortest[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            var pointIndex = mantissa.IndexOf('.');
            var integerPart = pointIndex < 0 ? mantissa : mantissa[..pointIndex];
            var all = integerPart + (pointIndex < 0 ? string.Empty : mantissa[(pointIndex + 1)..]);
            var leadingZeros = all.Length - all.TrimStart('0').Length;
            var digits = all.Trim('0');
            var point = integerPart.Length - leadingZeros + exponent;

            if (point > -4 && point <= 16)
            {
                if (point <= 0)
                    return sign + "0." + new string('0', -point) + digits;

                if (point >= digits.Length)
                    return sign + digits + new string('0', point - digits.Length) + ".0";

                return sign + digits[..point] + "." + digits[point..];
            }

            var power = point - 1;
            return sign + (digits.Length == 1 ? digits : digits[0] + "." + digits[1..])
                + "e" + (power < 0 ? "-" : "+") + Math.Abs(power).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Python 3 <c>repr</c> of a text: single quotes, double quotes when the text contains a single quote and no double quote.
        /// The printable characters (accents, emoji...) are kept.
        /// </summary>
        internal static string ReprString(string text)
        {
            var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
            var builder = new StringBuilder(text.Length + 2).Append(quote);

            foreach (var rune in text.EnumerateRunes())
            {
                switch (rune.Value)
                {
                    case '\\': builder.Append(@"\\"); break;
                    case '\n': builder.Append(@"\n"); break;
                    case '\r': builder.Append(@"\r"); break;
                    case '\t': builder.Append(@"\t"); break;
                    default:
                        if (rune.Value == quote)
                            builder.Append('\\').Append(quote);
                        else if (IsPrintable(rune))
                            builder.Append(rune.ToString());
                        else if (rune.Value < 0x100)
                            builder.Append(@"\x").Append(rune.Value.ToString("x2", CultureInfo.InvariantCulture));
                        else if (rune.Value < 0x10000)
                            builder.Append(@"\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(@"\U").Append(rune.Value.ToString("x8", CultureInfo.InvariantCulture));
                        break;
                }
            }

            return builder.Append(quote).ToString();
        }

        /// <summary>
        /// Python's <c>str.isprintable</c>: every character except the control, format, surrogate, private use and unassigned ones,
        /// and the separators other than the space (a non-breaking space is written <c>\xa0</c>).
        /// </summary>
        private static bool IsPrintable(Rune rune)
        {
            if (rune.Value == ' ')
                return true;

            return Rune.GetUnicodeCategory(rune) switch
            {
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    or UnicodeCategory.SpaceSeparator => false,
                _ => true,
            };
        }

        [GeneratedRegex(@"^\s*[\[(]\s*(\d+)\s*(?:,.*)?[\])]\s*$", RegexOptions.Singleline)]
        private static partial Regex Many2OnePattern();
    }
}
