using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using EvoLink.Connectors.Common;
using Ortolan.Core.Conditions;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers.Conditions
{
    /// <summary>Comparison of numbers, dates and sizes (EvoBus <c>ConditionArithmeticComparison</c>).</summary>
    public enum NumericComparison
    {
        Equals,
        NotEquals,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
    }

    /// <summary>Comparison of texts (EvoBus <c>ConditionStringComparison</c>).</summary>
    public enum TextComparison
    {
        Equals,
        NotEquals,
        Contains,
        NotContains,
    }

    public enum SizeUnit
    {
        Bytes,
        KB,
        MB,
        GB,
    }

    /// <summary>
    /// Base of the conditions whose value can be missing or not convertible. EvoBus threw in that case; with Ortolan a
    /// condition used in the criteria of a service is evaluated outside of the per-entity error handling, so the result can
    /// be fixed with <see cref="ResultWhenInvalid"/> instead (null keeps the explicit error).
    /// </summary>
    public abstract class ValueCondition : ICondition
    {
        [Description("Propriété évaluée.")]
        public string Property { get; set; } = string.Empty;

        [Description("Résultat lorsque la propriété est absente ou sa valeur non convertible (vide = erreur explicite).")]
        public bool? ResultWhenInvalid { get; set; }

        public abstract bool Match(Entity entity);

        protected string Name => GetType().GetCustomAttributes(typeof(OrtolanTypeAttribute), false).OfType<OrtolanTypeAttribute>().FirstOrDefault()?.Alias ?? GetType().Name;

        protected bool Invalid(string reason)
            => ResultWhenInvalid ?? throw new InvalidOperationException($"[{Name}] {reason}");

        protected void RequireProperty()
        {
            if (string.IsNullOrWhiteSpace(Property))
                throw new OrtolanConfigurationException($"[{Name}] La propriété 'property' de la condition est obligatoire.");
        }

        protected static bool Compare(int comparison, NumericComparison comparisonType)
            => comparisonType switch
            {
                NumericComparison.Equals => comparison == 0,
                NumericComparison.NotEquals => comparison != 0,
                NumericComparison.GreaterThan => comparison > 0,
                NumericComparison.GreaterThanOrEqual => comparison >= 0,
                NumericComparison.LessThan => comparison < 0,
                NumericComparison.LessThanOrEqual => comparison <= 0,
                _ => throw new NotSupportedException($"Comparaison '{comparisonType}' non prise en charge."),
            };
    }

    /// <summary>
    /// Port of the EvoBus <c>ArithmeticCondition</c>: compares a numeric property to a constant (decimal, culture-invariant).
    /// </summary>
    [OrtolanType("arithmetic-condition")]
    [Description("Compare une propriété numérique à une valeur.")]
    public class ArithmeticCondition : ValueCondition
    {
        [Description("Comparaison : Equals, NotEquals, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual.")]
        public NumericComparison Operator { get; set; } = NumericComparison.Equals;

        [Description("Valeur attendue.")]
        public decimal Value { get; set; }

        public override bool Match(Entity entity)
        {
            RequireProperty();

            if (!entity.TryGetValue(Property, out var raw))
                return Invalid($"Propriété '{Property}' absente.");

            return ValueConverter.ToDecimal(raw) is { } value
                ? Compare(value.CompareTo(Value), Operator)
                : Invalid($"La valeur '{Helpers.Text(raw)}' de la propriété '{Property}' n'est pas numérique.");
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>DateCondition</c>: compares a date property to a date. Differences: the date is parsed
    /// culture-invariantly (ISO and Swiss formats), the expected value accepts relative dates ("today-30", "now+1") and
    /// the comparison can ignore the time.
    /// </summary>
    [OrtolanType("date-condition")]
    [Description("Compare une propriété date à une date fixe ou relative (today, now, today-30, now+7...).")]
    public partial class DateCondition : ValueCondition
    {
        [Description("Comparaison : Equals, NotEquals, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual.")]
        public NumericComparison Operator { get; set; } = NumericComparison.Equals;

        [Description("Date attendue : date (yyyy-MM-dd, dd.MM.yyyy...) ou date relative 'today', 'now', suivie éventuellement de +N / -N jours.")]
        public string Value { get; set; } = "today";

        [Description("Compare uniquement les dates, sans l'heure.")]
        public bool DateOnly { get; set; }

        public override bool Match(Entity entity)
        {
            RequireProperty();

            var expected = ResolveExpected(Value, DateTime.Now);

            if (!entity.TryGetValue(Property, out var raw))
                return Invalid($"Propriété '{Property}' absente.");

            if (ValueConverter.ToDateTime(raw) is not { } actual)
                return Invalid($"La valeur '{Helpers.Text(raw)}' de la propriété '{Property}' n'est pas une date.");

            return DateOnly
                ? Compare(actual.Date.CompareTo(expected.Date), Operator)
                : Compare(actual.CompareTo(expected), Operator);
        }

        internal static DateTime ResolveExpected(string value, DateTime now)
        {
            var text = value?.Trim() ?? string.Empty;
            var relative = RelativeDateRegex().Match(text);

            if (relative.Success)
            {
                var origin = relative.Groups["origin"].Value.Equals("now", StringComparison.OrdinalIgnoreCase) ? now : now.Date;
                var offset = relative.Groups["offset"].Success ? int.Parse(relative.Groups["offset"].Value.Replace(" ", string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
                return origin.AddDays(offset);
            }

            return ValueConverter.ToDateTime(text)
                ?? throw new OrtolanConfigurationException($"[date-condition] Date attendue '{value}' invalide (formats acceptés : yyyy-MM-dd, dd.MM.yyyy, today, now, today-30...).");
        }

        [GeneratedRegex(@"^(?<origin>today|now)\s*(?<offset>[+-]\s*\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
        private static partial Regex RelativeDateRegex();
    }

    /// <summary>
    /// Port of the EvoBus <c>FileSizeCondition</c>: compares the size of a file (<c>FILE-ByteSize</c>, set by
    /// <c>file-info</c>) to a size. Differences: configurable unit (MB by default as in EvoBus) and exact comparison
    /// (EvoBus truncated the size to whole megabytes, a 1.5 MB file was "equal" to 1).
    /// </summary>
    [OrtolanType("file-size")]
    [Description("Compare la taille d'un fichier (FILE-ByteSize, ajoutée par file-info) à une taille.")]
    public class FileSizeCondition : ValueCondition
    {
        public FileSizeCondition()
        {
            Property = FileKeys.ByteSize;
        }

        [Description("Comparaison : Equals, NotEquals, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual.")]
        public NumericComparison Operator { get; set; } = NumericComparison.GreaterThanOrEqual;

        [Description("Taille de comparaison, dans l'unité 'unit'.")]
        public decimal Size { get; set; }

        [Description("Unité de 'size' : Bytes, KB, MB, GB (multiples de 1024).")]
        public SizeUnit Unit { get; set; } = SizeUnit.MB;

        public override bool Match(Entity entity)
        {
            RequireProperty();

            if (!entity.TryGetValue(Property, out var raw))
                return Invalid($"Propriété '{Property}' absente (ajoutez un transformer file-info avant la condition).");

            if (ValueConverter.ToDecimal(raw) is not { } bytes)
                return Invalid($"La taille '{Helpers.Text(raw)}' de la propriété '{Property}' n'est pas numérique.");

            var limit = Size * Unit switch
            {
                SizeUnit.KB => 1024m,
                SizeUnit.MB => 1024m * 1024m,
                SizeUnit.GB => 1024m * 1024m * 1024m,
                _ => 1m,
            };

            return Compare(bytes.CompareTo(limit), Operator);
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>PropertyStringContainsCondition</c>: true when the text of the property contains a value.
    /// A missing or empty property never matches.
    /// </summary>
    [OrtolanType("contains")]
    [Description("Vrai si le texte de la propriété contient la valeur (propriété absente ou vide : faux).")]
    public class ContainsCondition : ICondition
    {
        [Description("Propriété évaluée.")]
        public string Property { get; set; } = string.Empty;

        [Description("Texte recherché.")]
        public string Value { get; set; } = string.Empty;

        [Description("Ignore la casse (EvoBus : sensible à la casse).")]
        public bool IgnoreCase { get; set; }

        public bool Match(Entity entity)
        {
            if (string.IsNullOrWhiteSpace(Property))
                throw new OrtolanConfigurationException("[contains] La propriété 'property' de la condition est obligatoire.");

            var text = Helpers.Text(entity.GetValueOrDefault(Property));

            return text.Length > 0 && text.Contains(Value ?? string.Empty, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>PropertyValueComparisonCondition</c>: compares the text of a property to a value (equals,
    /// not equals, contains, not contains). A null value is compared as an empty text.
    /// </summary>
    [OrtolanType("property-compare")]
    [Description("Compare le texte d'une propriété à une valeur : Equals, NotEquals, Contains, NotContains.")]
    public class PropertyCompareCondition : ValueCondition
    {
        [Description("Comparaison : Equals, NotEquals, Contains, NotContains.")]
        public TextComparison Operator { get; set; } = TextComparison.Equals;

        [Description("Valeur attendue.")]
        public string Value { get; set; } = string.Empty;

        [Description("Ignore la casse (EvoBus : sensible à la casse).")]
        public bool IgnoreCase { get; set; }

        public override bool Match(Entity entity)
        {
            RequireProperty();

            if (!entity.TryGetValue(Property, out var raw))
                return Invalid($"Propriété '{Property}' absente.");

            var text = Helpers.Text(raw);
            var expected = Value ?? string.Empty;
            var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return Operator switch
            {
                TextComparison.Equals => string.Equals(text, expected, comparison),
                TextComparison.NotEquals => !string.Equals(text, expected, comparison),
                TextComparison.Contains => text.Contains(expected, comparison),
                TextComparison.NotContains => !text.Contains(expected, comparison),
                _ => throw new NotSupportedException($"Comparaison '{Operator}' non prise en charge."),
            };
        }
    }
}
