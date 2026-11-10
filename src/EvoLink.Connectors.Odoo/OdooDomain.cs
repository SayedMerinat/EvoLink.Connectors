using System.ComponentModel;
using EvoLink.Connectors.Common;
using EvoLink.Odoo.Client;
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
                JArray items;

                try
                {
                    items = OdooDomainBuilder.Parse(rawDomain).ToJArray();
                }
                catch (FormatException ex)
                {
                    throw ConnectorGuard.Invalid(module, ex.Message);
                }

                // Implicit AND with the filters (prefix notation).
                foreach (var item in items)
                    domain.Add(item);
            }

            return domain;
        }

        /// <summary>Leaf <c>[field, operator, value]</c>.</summary>
        public static JArray Condition(string field, string @operator, object? value)
            => new(field, @operator, OdooJson.ToJson(value));

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
