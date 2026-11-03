namespace EvoLink.Connectors.Odoo.Client
{
    /// <summary>
    /// Connection settings of an Odoo instance. <see cref="ToString"/> never exposes the API key, it can be logged.
    /// </summary>
    public sealed class OdooConnection
    {
        public required string Url { get; init; }
        public required string Database { get; init; }
        public required string Username { get; init; }
        public required string ApiKey { get; init; }
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

        public override string ToString()
            => $"{Url.Trim().TrimEnd('/')} (base {Database})";
    }

    /// <summary>
    /// Context sent with every <c>execute_kw</c> call (<c>lang</c>, <c>active_test</c>).
    /// </summary>
    public sealed class OdooContext
    {
        public static OdooContext Default { get; } = new();

        /// <summary>Language of the translatable fields, e.g. fr_CH (null = language of the user).</summary>
        public string? Language { get; init; }

        /// <summary>False to include the archived records (active = false) in the searches.</summary>
        public bool? ActiveTest { get; init; }

        internal Newtonsoft.Json.Linq.JObject ToJson()
        {
            var context = new Newtonsoft.Json.Linq.JObject();

            if (!string.IsNullOrWhiteSpace(Language))
                context["lang"] = Language.Trim();

            if (ActiveTest.HasValue)
                context["active_test"] = ActiveTest.Value;

            return context;
        }
    }

    /// <summary>Description of a field returned by <c>fields_get</c>.</summary>
    public sealed record OdooFieldInfo(string Name, string Type, string? Relation = null, bool Readonly = false, bool Store = true, bool Required = false, string? Label = null);

    /// <summary>Result of a <c>name_search</c>.</summary>
    public sealed record OdooNameResult(long Id, string Name);
}
