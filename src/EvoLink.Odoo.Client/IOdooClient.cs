using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Access to an Odoo 17 instance: the ORM methods called through <c>execute_kw</c>. <see cref="OdooClientExtensions"/> adds
    /// the usual operations on top of them (read every page, find one record, create or update, delete by domain...).
    /// <para>
    /// The records are returned with plain .NET values: integers as <see cref="long"/>, floats as <see cref="double"/>, lists as
    /// <c>List&lt;object?&gt;</c>, objects as dictionaries, dates as the strings sent by Odoo (UTC for the datetimes) and
    /// <c>false</c> for an empty field, exactly like Odoo. <see cref="OdooRecordExtensions"/> reads them with their type.
    /// </para>
    /// <para>
    /// Failures: <see cref="OdooUnavailableException"/> when Odoo can't be reached or refuses the credentials,
    /// <see cref="OdooConfigurationException"/> for wrong connection settings, <see cref="OdooRpcException"/> for a business error.
    /// A cancelled <see cref="CancellationToken"/> gives an <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public interface IOdooClient : IDisposable
    {
        /// <summary><c>fields_get</c>: fields of the model by name.</summary>
        Task<IReadOnlyDictionary<string, OdooFieldInfo>> FieldsGetAsync(string model, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>search_read</c> of one page (<see cref="OdooClientExtensions.SearchReadAllAsync"/> reads every page).</summary>
        /// <param name="fields">Fields read (null or empty = all the fields, which can be slow).</param>
        /// <param name="limit">Maximum number of records (0 = no limit).</param>
        Task<IReadOnlyList<Dictionary<string, object?>>> SearchReadAsync(string model, JArray domain, IReadOnlyCollection<string>? fields, int offset, int limit,
            string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>search</c>: identifiers of the matching records.</summary>
        Task<IReadOnlyList<long>> SearchAsync(string model, JArray domain, int? limit = null, string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>create</c> of a batch of records (one call): identifiers in the same order.</summary>
        Task<IReadOnlyList<long>> CreateAsync(string model, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>write</c> of the same values on the given records (one call).</summary>
        Task WriteAsync(string model, IReadOnlyCollection<long> ids, IReadOnlyDictionary<string, object?> values, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>unlink</c> of the given records (one call).</summary>
        Task UnlinkAsync(string model, IReadOnlyCollection<long> ids, OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary><c>name_search</c>: records whose display name matches <paramref name="name"/>.</summary>
        Task<IReadOnlyList<OdooNameResult>> NameSearchAsync(string model, string name, string @operator = "ilike", JArray? domain = null, int limit = 100,
            OdooContext? context = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Any other method of a model (<c>search_count</c>, <c>action_archive</c>, <c>action_confirm</c>...) with its positional and
        /// keyword arguments. The context is added to <paramref name="kwargs"/> unless it already has one.
        /// </summary>
        Task<JToken> ExecuteKwAsync(string model, string method, JArray args, JObject? kwargs = null, OdooContext? context = null, CancellationToken cancellationToken = default);
    }
}
