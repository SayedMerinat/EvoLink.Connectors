using System.ComponentModel;
using EvoLink.Connectors.Common;
using EvoLink.Connectors.Odoo.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>
    /// Criterion of an Odoo domain, e.g. <c>{ "field": "company_id", "operator": "=", "value": 1 }</c>.
    /// </summary>
    public class OdooDomainFilter
    {
        [Description("Champ Odoo filtré.")]
        public string Field { get; set; } = string.Empty;

        [Description("Opérateur Odoo : =, !=, >, >=, <, <=, like, ilike, =like, =ilike, in, not in, child_of...")]
        public string Operator { get; set; } = "=";

        [Description("Valeur comparée (liste pour in / not in, false pour un champ vide).")]
        public object? Value { get; set; }
    }

    /// <summary>
    /// Builds the Odoo domains (prefix notation, implicit AND between the criteria).
    /// </summary>
    public static class OdooDomain
    {
        private static readonly HashSet<string> LogicalOperators = ["&", "|", "!"];

        /// <summary>
        /// Domain made of the filters and of an optional raw JSON domain (e.g. <c>["|", ["a", "=", 1], ["b", "=", 2]]</c>), all of them
        /// having to match. Throws an <see cref="Ortolan.Core.Exceptions.OrtolanConfigurationException"/> when they are invalid.
        /// </summary>
        public static JArray Build(IEnumerable<OdooDomainFilter>? filters, string? rawDomain, string module)
        {
            var domain = new JArray();

            foreach (var filter in filters ?? [])
            {
                if (filter == null || string.IsNullOrWhiteSpace(filter.Field) || string.IsNullOrWhiteSpace(filter.Operator))
                    throw ConnectorGuard.Invalid(module, "Un filtre Odoo doit indiquer un champ et un opérateur.");

                domain.Add(Condition(filter.Field.Trim(), filter.Operator.Trim(), ValueNormalizer.Normalize(filter.Value)));
            }

            if (!string.IsNullOrWhiteSpace(rawDomain))
            {
                JToken parsed;

                try
                {
                    parsed = JToken.Parse(rawDomain);
                }
                catch (JsonException ex)
                {
                    throw ConnectorGuard.Invalid(module, $"Le domaine Odoo n'est pas un tableau JSON valide : {ex.Message}");
                }

                if (parsed is not JArray items)
                    throw ConnectorGuard.Invalid(module, "Le domaine Odoo doit être un tableau JSON, ex. [[\"active\", \"=\", true]].");

                foreach (var item in items)
                {
                    var valid = item switch
                    {
                        JValue { Type: JTokenType.String } op => LogicalOperators.Contains(op.Value<string>()!),
                        JArray { Count: 3 } leaf => leaf[0].Type == JTokenType.String && leaf[1].Type == JTokenType.String,
                        _ => false,
                    };

                    if (!valid)
                        throw ConnectorGuard.Invalid(module, $"Élément de domaine Odoo invalide : {item.ToString(Formatting.None)} (attendu : [\"champ\", \"opérateur\", valeur], \"&\", \"|\" ou \"!\").");

                    domain.Add(item.DeepClone());
                }
            }

            return domain;
        }

        /// <summary>Leaf <c>[field, operator, value]</c>.</summary>
        public static JArray Condition(string field, string @operator, object? value)
            => new(field, @operator, OdooJsonRpcClient.ToJson(value));

        /// <summary>Copy of <paramref name="domain"/> with additional leaves (implicit AND).</summary>
        public static JArray And(JArray domain, params JArray[] leaves)
        {
            var result = (JArray)domain.DeepClone();

            foreach (var leaf in leaves)
                result.Add(leaf);

            return result;
        }
    }
}
