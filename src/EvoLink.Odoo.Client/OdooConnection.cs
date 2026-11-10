using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Connection settings of an Odoo instance. <see cref="ToString"/> never exposes the API key, it can be logged.
    /// </summary>
    public sealed class OdooConnection
    {
        /// <summary>Address of the instance, e.g. https://societe.odoo.com (a trailing /jsonrpc is accepted).</summary>
        public required string Url { get; init; }

        public required string Database { get; init; }

        /// <summary>Login of the user.</summary>
        public required string Username { get; init; }

        /// <summary>API key of the user (or its password).</summary>
        public required string ApiKey { get; init; }

        /// <summary>Maximum duration of one call.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);

        /// <summary>JSON-RPC endpoint: <c>{url}/jsonrpc</c> (the suffix is not added twice).</summary>
        public Uri Endpoint
        {
            get
            {
                var root = Url.Trim().TrimEnd('/');

                return new Uri(root.EndsWith("/jsonrpc", StringComparison.OrdinalIgnoreCase) ? root : root + "/jsonrpc");
            }
        }

        /// <summary>
        /// Checks the settings before the first call.
        /// </summary>
        /// <exception cref="OdooConfigurationException">A setting is missing or the address is not an absolute http(s) URL.</exception>
        public void Validate()
        {
            var missing = new[] { (nameof(Url), Url), (nameof(Database), Database), (nameof(Username), Username), (nameof(ApiKey), ApiKey) }
                .Where(s => string.IsNullOrWhiteSpace(s.Item2))
                .Select(s => s.Item1)
                .ToList();

            if (missing.Count > 0)
                throw new OdooConfigurationException($"Connexion Odoo incomplète : {string.Join(", ", missing)} manquant(s).");

            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new OdooConfigurationException($"L'URL Odoo '{Url}' n'est pas une adresse http(s) absolue, ex. https://societe.odoo.com.");

            if (Timeout <= TimeSpan.Zero)
                throw new OdooConfigurationException("Le délai d'attente d'un appel à Odoo doit être supérieur à 0.");
        }

        public override string ToString()
            => $"{Url.Trim().TrimEnd('/')} (base {Database})";
    }

    /// <summary>
    /// Context sent with every <c>execute_kw</c> call.
    /// </summary>
    public sealed class OdooContext
    {
        public static OdooContext Default { get; } = new();

        /// <summary>Language of the translatable fields, e.g. fr_CH (null = language of the user).</summary>
        public string? Language { get; init; }

        /// <summary>False to include the archived records (active = false) in the searches.</summary>
        public bool? ActiveTest { get; init; }

        /// <summary>Time zone of the user for the computations done by Odoo, e.g. Europe/Zurich (the datetimes exchanged stay in UTC).</summary>
        public string? TimeZone { get; init; }

        /// <summary>Other keys of the context (company, default values: <c>default_type</c>...), sent as they are.</summary>
        public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();

        /// <summary>Copy of this context including the archived records.</summary>
        public OdooContext WithArchived()
            => new() { Language = Language, ActiveTest = false, TimeZone = TimeZone, Values = Values };

        internal JObject ToJson()
        {
            var context = new JObject();

            foreach (var (key, value) in Values)
                context[key] = OdooJson.ToJson(value);

            if (!string.IsNullOrWhiteSpace(Language))
                context["lang"] = Language.Trim();

            if (ActiveTest.HasValue)
                context["active_test"] = ActiveTest.Value;

            if (!string.IsNullOrWhiteSpace(TimeZone))
                context["tz"] = TimeZone.Trim();

            return context;
        }
    }

    /// <summary>
    /// Description of a field returned by <c>fields_get</c>.
    /// </summary>
    /// <param name="Name">Technical name.</param>
    /// <param name="Type">Odoo type: char, integer, float, monetary, boolean, date, datetime, selection, many2one, many2many, one2many...</param>
    /// <param name="Relation">Model of a relational field.</param>
    /// <param name="Readonly">Read-only in the forms (a stored read-only field can still be written by the API).</param>
    /// <param name="Store">False for a computed field that is not stored.</param>
    /// <param name="Required">Mandatory at creation.</param>
    /// <param name="Label">Label shown to the users.</param>
    /// <param name="Selection">Allowed values of a selection field (technical value, label).</param>
    public sealed record OdooFieldInfo(string Name, string Type, string? Relation = null, bool Readonly = false, bool Store = true, bool Required = false,
        string? Label = null, IReadOnlyList<KeyValuePair<string, string>>? Selection = null)
    {
        /// <summary>Fields maintained by Odoo itself, never written.</summary>
        public static readonly IReadOnlySet<string> MagicFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "display_name", "create_date", "create_uid", "write_date", "write_uid", "__last_update",
        };

        /// <summary>
        /// True when a value can be written with <c>create</c> / <c>write</c>: not a magic field, not a one2many (it needs commands),
        /// not a computed field without inverse (read-only and not stored).
        /// </summary>
        public bool IsWritable
            => !MagicFields.Contains(Name) && Type != "one2many" && !(Readonly && !Store);
    }

    /// <summary>Result of a <c>name_search</c>.</summary>
    public sealed record OdooNameResult(long Id, string Name);
}
