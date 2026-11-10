using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Builds an Odoo domain (prefix notation) without writing its JSON by hand. Immutable: every method returns a new domain.
    /// <code>
    /// var domain = OdooDomainBuilder.Where("is_company", "=", true)
    ///     .And(OdooDomainBuilder.Where("city", "in", new[] { "Lausanne", "Genève" }).Or("country_id.code", "=", "CH"));
    /// // ["&amp;", ["is_company", "=", true], "|", ["city", "in", ["Lausanne", "Genève"]], ["country_id.code", "=", "CH"]]
    /// </code>
    /// A domain converts implicitly to the <see cref="JArray"/> expected by <see cref="IOdooClient"/>.
    /// </summary>
    public sealed class OdooDomainBuilder
    {
        private static readonly HashSet<string> LogicalOperators = ["&", "|", "!"];

        private readonly JArray _terms;

        private OdooDomainBuilder(JArray terms)
        {
            _terms = terms;
        }

        /// <summary>Domain matching every record.</summary>
        public static OdooDomainBuilder All { get; } = new([]);

        /// <summary>True for the domain matching every record.</summary>
        public bool IsEmpty => _terms.Count == 0;

        /// <summary>Leaf <c>[field, operator, value]</c>, e.g. <c>Where("name", "ilike", "evo")</c>; <c>false</c> designates an empty field.</summary>
        public static OdooDomainBuilder Where(string field, string @operator, object? value)
        {
            if (string.IsNullOrWhiteSpace(field))
                throw new ArgumentException("Le champ d'un critère de domaine est obligatoire.", nameof(field));

            if (string.IsNullOrWhiteSpace(@operator))
                throw new ArgumentException("L'opérateur d'un critère de domaine est obligatoire.", nameof(@operator));

            return new([new JArray(field.Trim(), @operator.Trim(), OdooJson.ToJson(value))]);
        }

        /// <summary><c>[field, "=", value]</c>.</summary>
        public static OdooDomainBuilder Equal(string field, object? value)
            => Where(field, "=", value);

        /// <summary><c>[field, "in", values]</c>.</summary>
        public static OdooDomainBuilder In<T>(string field, IEnumerable<T> values)
            => Where(field, "in", values.Cast<object?>().ToList());

        /// <summary><c>[field, "ilike", text]</c> (contains, case-insensitive).</summary>
        public static OdooDomainBuilder Contains(string field, string text)
            => Where(field, "ilike", text);

        /// <summary><c>["id", "in", ids]</c>.</summary>
        public static OdooDomainBuilder Ids(IEnumerable<long> ids)
            => Where("id", "in", ids.Select(i => (object?)i).ToList());

        /// <summary>
        /// Domain written in JSON, e.g. <c>["|", ["a", "=", 1], ["b", "=", 2]]</c>.
        /// </summary>
        /// <exception cref="FormatException">Not a JSON array of leaves and logical operators.</exception>
        public static OdooDomainBuilder Parse(string json)
        {
            JToken parsed;

            try
            {
                parsed = JToken.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Le domaine Odoo n'est pas un tableau JSON valide : {ex.Message}", ex);
            }

            if (parsed is not JArray items)
                throw new FormatException("Le domaine Odoo doit être un tableau JSON, ex. [[\"active\", \"=\", true]].");

            return From(items);
        }

        /// <summary>Domain from its JSON form (validated, copied).</summary>
        /// <exception cref="FormatException">An item is neither a leaf <c>[field, operator, value]</c> nor "&amp;", "|", "!".</exception>
        public static OdooDomainBuilder From(JArray domain)
        {
            foreach (var item in domain)
            {
                var valid = item switch
                {
                    JValue { Type: JTokenType.String } op => LogicalOperators.Contains(op.Value<string>()!),
                    JArray { Count: 3 } leaf => leaf[0].Type == JTokenType.String && leaf[1].Type == JTokenType.String,
                    _ => false,
                };

                if (!valid)
                    throw new FormatException($"Élément de domaine Odoo invalide : {item.ToString(Formatting.None)} (attendu : [\"champ\", \"opérateur\", valeur], \"&\", \"|\" ou \"!\").");
            }

            return new((JArray)domain.DeepClone());
        }

        /// <summary>Both domains must match.</summary>
        public OdooDomainBuilder And(OdooDomainBuilder other)
            => Combine("&", this, other);

        public OdooDomainBuilder And(string field, string @operator, object? value)
            => And(Where(field, @operator, value));

        /// <summary>At least one of the domains must match.</summary>
        public OdooDomainBuilder Or(OdooDomainBuilder other)
            => Combine("|", this, other);

        public OdooDomainBuilder Or(string field, string @operator, object? value)
            => Or(Where(field, @operator, value));

        /// <summary>The domain must not match.</summary>
        public static OdooDomainBuilder Not(OdooDomainBuilder domain)
            => domain.IsEmpty ? throw new ArgumentException("Impossible de nier le domaine vide.", nameof(domain)) : new([.. new JToken[] { "!" }, .. Normalized(domain)]);

        /// <summary>Every domain must match.</summary>
        public static OdooDomainBuilder AllOf(params OdooDomainBuilder[] domains)
            => domains.Aggregate(All, (current, next) => current.And(next));

        /// <summary>At least one of the domains must match.</summary>
        public static OdooDomainBuilder AnyOf(params OdooDomainBuilder[] domains)
            => domains.Length == 0 ? throw new ArgumentException("Au moins un domaine est nécessaire.", nameof(domains)) : domains.Skip(1).Aggregate(domains[0], (current, next) => current.Or(next));

        /// <summary>JSON form sent to Odoo (a copy).</summary>
        public JArray ToJArray()
            => (JArray)_terms.DeepClone();

        public static implicit operator JArray(OdooDomainBuilder domain)
            => domain.ToJArray();

        public override string ToString()
            => _terms.ToString(Formatting.None);

        private static OdooDomainBuilder Combine(string @operator, OdooDomainBuilder left, OdooDomainBuilder right)
        {
            if (left.IsEmpty)
                return right;

            if (right.IsEmpty)
                return left;

            return new([.. new JToken[] { @operator }, .. Normalized(left), .. Normalized(right)]);
        }

        /// <summary>
        /// The terms of a domain as one expression: an implicit AND of several leaves (["a", "b"]) is made explicit (["&amp;", "a", "b"])
        /// so that it can be an operand of "|" or "!".
        /// </summary>
        private static IEnumerable<JToken> Normalized(OdooDomainBuilder domain)
        {
            var terms = domain._terms.Select(t => t.DeepClone()).ToList();
            var expressions = CountExpressions(terms);

            return expressions <= 1 ? terms : [.. Enumerable.Repeat<JToken>("&", expressions - 1), .. terms];
        }

        /// <summary>Number of top-level expressions of a domain in prefix notation.</summary>
        private static int CountExpressions(IReadOnlyList<JToken> terms)
        {
            var index = 0;
            var expressions = 0;

            while (index < terms.Count)
            {
                SkipExpression(terms, ref index);
                expressions++;
            }

            return expressions;
        }

        /// <summary>Moves <paramref name="index"/> after one expression: a leaf, "!" and its operand, or "&amp;" / "|" and their two operands.</summary>
        private static void SkipExpression(IReadOnlyList<JToken> terms, ref int index)
        {
            if (index >= terms.Count)
                return;

            var term = terms[index++];

            if (term is not JValue { Type: JTokenType.String } op)
                return;

            var operands = op.Value<string>() == "!" ? 1 : 2;
            for (var i = 0; i < operands; i++)
                SkipExpression(terms, ref index);
        }
    }
}
