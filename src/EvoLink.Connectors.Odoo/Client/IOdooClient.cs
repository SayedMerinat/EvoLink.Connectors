using Newtonsoft.Json.Linq;

namespace EvoLink.Connectors.Odoo.Client
{
    /// <summary>
    /// Access to an Odoo 17 instance (ORM methods called through <c>execute_kw</c>). The records are returned with normalised
    /// values: integers as <see cref="long"/>, floats as <see cref="double"/>, lists as <c>List&lt;object?&gt;</c>, dates as
    /// the strings sent by Odoo (UTC for the datetimes) and <c>false</c> for an empty field, exactly like Odoo.
    /// <para>
    /// Failures: <see cref="OdooUnavailableException"/> when Odoo can't be reached or refuses the credentials,
    /// <see cref="OdooRpcException"/> for a business error.
    /// </para>
    /// </summary>
    public interface IOdooClient : IDisposable
    {
        /// <summary><c>fields_get</c>: fields of the model by name.</summary>
        Task<IReadOnlyDictionary<string, OdooFieldInfo>> FieldsGetAsync(string model, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>search_read</c> of one page (use <see cref="OdooClientExtensions.SearchReadAllAsync"/> to read every page).</summary>
        Task<IReadOnlyList<Dictionary<string, object?>>> SearchReadAsync(string model, JArray domain, IReadOnlyCollection<string>? fields, int offset, int limit,
            string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>search</c>: identifiers of the matching records.</summary>
        Task<IReadOnlyList<long>> SearchAsync(string model, JArray domain, int? limit = null, string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>create</c> of a batch of records: identifiers in the same order.</summary>
        Task<IReadOnlyList<long>> CreateAsync(string model, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>write</c> of the same values on the given records.</summary>
        Task WriteAsync(string model, IReadOnlyCollection<long> ids, IReadOnlyDictionary<string, object?> values, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>unlink</c> of the given records.</summary>
        Task UnlinkAsync(string model, IReadOnlyCollection<long> ids, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>name_search</c>: records whose display name matches <paramref name="name"/>.</summary>
        Task<IReadOnlyList<OdooNameResult>> NameSearchAsync(string model, string name, string @operator = "ilike", JArray? domain = null, int limit = 100,
            OdooContext? context = null, CancellationToken cancellationToken = default);
    }

    public static class OdooClientExtensions
    {
        /// <summary>
        /// Reads every page of a <c>search_read</c> (<paramref name="pageSize"/> records per call), up to <paramref name="maxRecords"/> (0 = all).
        /// </summary>
        public static async Task<List<Dictionary<string, object?>>> SearchReadAllAsync(this IOdooClient client, string model, JArray domain, IReadOnlyCollection<string>? fields,
            int pageSize = 500, int maxRecords = 0, string? order = "id", OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            pageSize = pageSize > 0 ? pageSize : 500;

            var records = new List<Dictionary<string, object?>>();

            while (true)
            {
                var limit = maxRecords > 0 ? Math.Min(pageSize, maxRecords - records.Count) : pageSize;
                if (limit <= 0)
                    break;

                var page = await client.SearchReadAsync(model, domain, fields, records.Count, limit, order, context, cancellationToken);
                records.AddRange(page);

                if (page.Count < limit)
                    break;
            }

            return records;
        }
    }
}
