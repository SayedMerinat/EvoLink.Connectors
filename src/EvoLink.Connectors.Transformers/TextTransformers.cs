using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Port of the EvoBus <c>Md5HashTransformer</c>: MD5 (lowercase hexadecimal) of the concatenation of properties.
    /// Differences: the properties are concatenated in the configured order (EvoBus used the order of the entity), the
    /// values are converted culture-invariantly (dates "yyyy-MM-dd HH:mm:ss", numbers with '.'), so a hash can differ from
    /// the one computed by EvoBus for dates, decimals or several properties.
    /// </summary>
    [OrtolanType("md5-hash")]
    [Description("Calcule l'empreinte MD5 (hexadécimal minuscule) de la concaténation de propriétés.")]
    public class Md5HashTransformer : TransformerModule
    {
        [Description("Propriétés concaténées, dans cet ordre.")]
        public List<string> Properties { get; set; } = [];

        [Description("Propriété recevant l'empreinte.")]
        public string To { get; set; } = string.Empty;

        [Description("Séparateur inséré entre les valeurs (vide = concaténation directe, comme EvoBus).")]
        public string Separator { get; set; } = string.Empty;

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("properties", Properties), ("to", To));

            var missing = Properties.Where(p => !entity.Has(p)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Propriété(s) à concaténer absente(s) de l'entité : {string.Join(", ", missing)}.");

            var concatenated = string.Join(Separator, Properties.Select(p => Helpers.Text(entity[p])));

            entity[To] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(concatenated)));

            return Task.FromResult<Entity?>(entity);
        }
    }

    public enum AggregationMode
    {
        /// <summary>Appends a constant text.</summary>
        RawValue,

        /// <summary>Appends the value of another property.</summary>
        PropertyValue,
    }

    /// <summary>
    /// Port of the EvoBus <c>PropertyAggregationTransformer</c> (configured in code with a lambda in EvoBus, the console
    /// configurator offered the two modes kept here): appends a separator and a constant or the value of another property
    /// to a property.
    /// </summary>
    [OrtolanType("aggregate")]
    [Description("Ajoute à la fin d'une propriété un séparateur suivi d'une valeur constante ou de la valeur d'une autre propriété.")]
    public class AggregateTransformer : TransformerModule
    {
        [Description("Propriété complétée.")]
        public string Property { get; set; } = string.Empty;

        [Description("Source de la valeur ajoutée : RawValue (constante 'value') ou PropertyValue (propriété 'sourceProperty').")]
        public AggregationMode Mode { get; set; } = AggregationMode.PropertyValue;

        [Description("Valeur constante ajoutée (mode RawValue).")]
        public string? Value { get; set; }

        [Description("Propriété dont la valeur est ajoutée (mode PropertyValue).")]
        public string? SourceProperty { get; set; }

        [Description("Texte inséré avant la valeur ajoutée.")]
        public string Separator { get; set; } = string.Empty;

        [Description("N'insère le séparateur que si les deux parties sont non vides (évite \"a;\" ou \";b\").")]
        public bool IgnoreEmpty { get; set; }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("property", Property));

            if (Mode == AggregationMode.PropertyValue)
                ConnectorGuard.Required(DisplayName, ("sourceProperty", SourceProperty));

            var current = Helpers.Text(Helpers.GetRequired(entity, Property));
            var added = Mode == AggregationMode.RawValue ? Value ?? string.Empty : Helpers.Text(Helpers.GetRequired(entity, SourceProperty!));

            entity[Property] = IgnoreEmpty && (current.Length == 0 || added.Length == 0)
                ? current + added
                : current + Separator + added;

            return Task.FromResult<Entity?>(entity);
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>RegexAggregatorTransformer</c>: replaces a property by its matches (or by the groups of its
    /// matches) joined with a separator; no match gives an empty text.
    /// Differences: optional destination property, regex timeout, case option, a missing property fails the entity, and
    /// with <see cref="UseGroups"/> the groups of every match are kept (EvoBus returned an empty text with several matches).
    /// </summary>
    [OrtolanType("regex-aggregate")]
    [Description("Remplace une propriété par toutes les correspondances d'une expression régulière (ou leurs groupes), jointes par un séparateur.")]
    public class RegexAggregateTransformer : TransformerModule
    {
        private Regex? _regex;

        [Description("Propriété analysée.")]
        public string From { get; set; } = string.Empty;

        [Description("Propriété recevant le résultat (vide = From).")]
        public string? To { get; set; }

        [Description("Expression régulière.")]
        public string Pattern { get; set; } = string.Empty;

        [Description("Séparateur entre les correspondances.")]
        public string Separator { get; set; } = string.Empty;

        [Description("Utilise les groupes capturés (non vides) au lieu des correspondances complètes.")]
        public bool UseGroups { get; set; }

        [Description("Ignore la casse.")]
        public bool IgnoreCase { get; set; }

        [Description("Délai maximal d'évaluation de l'expression, en millisecondes.")]
        public int TimeoutMs { get; set; } = Helpers.DefaultRegexTimeoutMs;

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("from", From));

            var regex = _regex ??= Helpers.CreateRegex(DisplayName, Pattern, IgnoreCase, TimeoutMs);
            var matches = regex.Matches(Helpers.Text(Helpers.GetRequired(entity, From)));

            var parts = UseGroups
                ? matches.SelectMany(m => m.Groups.Cast<Group>().Skip(1).Where(g => g.Success && g.Value.Length > 0).Select(g => g.Value))
                : matches.Select(m => m.Value);

            entity[string.IsNullOrWhiteSpace(To) ? From : To] = string.Join(Separator, parts);

            return Task.FromResult<Entity?>(entity);
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>RegexSingleMatchTransformer</c>: copies the first match of a regular expression (or one of its
    /// groups) into a property. As in EvoBus, an entity without the property or without match is left untouched.
    /// </summary>
    [OrtolanType("regex-extract")]
    [Description("Copie la première correspondance d'une expression régulière (ou l'un de ses groupes) dans une propriété.")]
    public class RegexExtractTransformer : TransformerModule
    {
        private Regex? _regex;

        [Description("Propriété analysée.")]
        public string From { get; set; } = string.Empty;

        [Description("Propriété recevant la correspondance.")]
        public string To { get; set; } = string.Empty;

        [Description("Expression régulière.")]
        public string Pattern { get; set; } = string.Empty;

        [Description("Groupe à extraire (numéro ou nom ; vide = correspondance complète).")]
        public string? Group { get; set; }

        [Description("Ignore la casse.")]
        public bool IgnoreCase { get; set; }

        [Description("Délai maximal d'évaluation de l'expression, en millisecondes.")]
        public int TimeoutMs { get; set; } = Helpers.DefaultRegexTimeoutMs;

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("from", From), ("to", To));

            var regex = _regex ??= CreateRegex();

            if (!entity.TryGetValue(From, out var value))
            {
                context.LogDebug($"[{DisplayName}] Propriété '{From}' absente, entité inchangée.");
                return Task.FromResult<Entity?>(entity);
            }

            var match = regex.Match(Helpers.Text(value));

            if (!match.Success)
                return Task.FromResult<Entity?>(entity);

            var group = string.IsNullOrWhiteSpace(Group) ? match.Groups[0] : match.Groups[Group.Trim()];

            if (group.Success)
                entity[To] = group.Value;

            return Task.FromResult<Entity?>(entity);
        }

        private Regex CreateRegex()
        {
            var regex = Helpers.CreateRegex(DisplayName, Pattern, IgnoreCase, TimeoutMs);

            if (!string.IsNullOrWhiteSpace(Group) && regex.GroupNumberFromName(Group.Trim()) < 0
                && !(int.TryParse(Group.Trim(), out var number) && regex.GetGroupNumbers().Contains(number)))
                throw ConnectorGuard.Invalid(DisplayName, $"Le groupe '{Group}' n'existe pas dans l'expression '{Pattern}'.");

            return regex;
        }
    }
}
