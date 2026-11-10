using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Usual operations built on the ORM methods of <see cref="IOdooClient"/>.
    /// <list type="bullet">
    /// <item>Finding nothing is a normal result (null or an empty list), never an error.</item>
    /// <item>A failure of Odoo is always thrown: an operation never falls back on another one (no creation because a search failed).</item>
    /// <item>Large reads are paged.</item>
    /// </list>
    /// </summary>
    public static class OdooClientExtensions
    {
        /// <summary>Number of identifiers asked to detect an ambiguous search (one more than needed, a few for the message).</summary>
        internal const int AmbiguityProbe = 5;

        /// <summary>
        /// Reads every page of a <c>search_read</c> (<paramref name="pageSize"/> records per call), up to <paramref name="maxRecords"/> (0 = all).
        /// The default order by id keeps the pages stable while records are added.
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

        /// <summary>Records matching <paramref name="domain"/> (every page).</summary>
        public static Task<List<Dictionary<string, object?>>> FindAsync(this IOdooClient client, string model, OdooDomainBuilder domain, IReadOnlyCollection<string>? fields = null,
            OdooContext? context = null, CancellationToken cancellationToken = default)
            => client.SearchReadAllAsync(model, domain, fields, context: context, cancellationToken: cancellationToken);

        /// <summary>The only record matching <paramref name="domain"/>, null when there is none.</summary>
        /// <exception cref="OdooAmbiguousResultException">Several records match.</exception>
        public static async Task<Dictionary<string, object?>?> FindOneAsync(this IOdooClient client, string model, OdooDomainBuilder domain, IReadOnlyCollection<string>? fields = null,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var records = await client.SearchReadAsync(model, domain, fields, 0, AmbiguityProbe, "id", context, cancellationToken);

            return records.Count switch
            {
                0 => null,
                1 => records[0],
                _ => throw new OdooAmbiguousResultException(model, records.Select(r => r.GetId("id") ?? 0).ToList()),
            };
        }

        /// <summary>Identifier of the only record matching <paramref name="domain"/>, null when there is none.</summary>
        /// <exception cref="OdooAmbiguousResultException">Several records match.</exception>
        public static async Task<long?> FindIdAsync(this IOdooClient client, string model, OdooDomainBuilder domain, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var ids = await client.SearchAsync(model, domain, AmbiguityProbe, "id", context, cancellationToken);

            return ids.Count switch
            {
                0 => null,
                1 => ids[0],
                _ => throw new OdooAmbiguousResultException(model, ids),
            };
        }

        /// <summary>Identifiers of the records matching <paramref name="domain"/> (empty when there is none).</summary>
        public static Task<IReadOnlyList<long>> FindIdsAsync(this IOdooClient client, string model, OdooDomainBuilder domain, OdooContext? context = null, CancellationToken cancellationToken = default)
            => client.SearchAsync(model, domain, null, "id", context, cancellationToken);

        /// <summary>Number of records matching <paramref name="domain"/> (<c>search_count</c>).</summary>
        public static async Task<long> CountAsync(this IOdooClient client, string model, OdooDomainBuilder domain, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var result = await client.ExecuteKwAsync(model, "search_count", [domain.ToJArray()], null, context, cancellationToken);
            return result.Type == JTokenType.Integer ? result.Value<long>() : 0;
        }

        /// <summary>Record <paramref name="id"/>.</summary>
        /// <exception cref="OdooRecordNotFoundException">It doesn't exist (or is archived, or not visible).</exception>
        public static async Task<Dictionary<string, object?>> GetAsync(this IOdooClient client, string model, long id, IReadOnlyCollection<string>? fields = null,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var records = await client.SearchReadAsync(model, OdooDomainBuilder.Ids([id]), fields, 0, 1, null, context, cancellationToken);
            return records.Count > 0 ? records[0] : throw new OdooRecordNotFoundException(model, id);
        }

        /// <summary>Records <paramref name="ids"/> that exist (in the order of <paramref name="ids"/>).</summary>
        public static async Task<List<Dictionary<string, object?>>> ReadAsync(this IOdooClient client, string model, IReadOnlyCollection<long> ids, IReadOnlyCollection<string>? fields = null,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (ids.Count == 0)
                return [];

            var records = await client.SearchReadAllAsync(model, OdooDomainBuilder.Ids(ids), fields, context: context, cancellationToken: cancellationToken);
            var byId = records.Where(r => r.GetId("id") is not null).ToDictionary(r => r.GetId("id")!.Value);

            return ids.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        /// <summary>Creates one record, returns its identifier.</summary>
        public static async Task<long> CreateAsync(this IOdooClient client, string model, IReadOnlyDictionary<string, object?> values, OdooContext? context = null, CancellationToken cancellationToken = default)
            => (await client.CreateAsync(model, [values], context, cancellationToken))[0];

        /// <summary>Writes <paramref name="values"/> on record <paramref name="id"/>.</summary>
        public static Task UpdateAsync(this IOdooClient client, string model, long id, IReadOnlyDictionary<string, object?> values, OdooContext? context = null, CancellationToken cancellationToken = default)
            => client.WriteAsync(model, [id], values, context, cancellationToken);

        /// <summary>Writes <paramref name="values"/> on every record matching <paramref name="domain"/> (one call), returns their number.</summary>
        public static async Task<int> UpdateWhereAsync(this IOdooClient client, string model, OdooDomainBuilder domain, IReadOnlyDictionary<string, object?> values,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var ids = await client.SearchAsync(model, domain, null, "id", context, cancellationToken);
            await client.WriteAsync(model, ids, values, context, cancellationToken);
            return ids.Count;
        }

        /// <summary>
        /// Updates the only record matching <paramref name="domain"/>, or creates it when there is none. A failure of the search is
        /// thrown: nothing is created when it is not sure that the record doesn't exist.
        /// </summary>
        /// <exception cref="OdooAmbiguousResultException">Several records match: nothing is written.</exception>
        public static async Task<OdooUpsertResult> UpsertAsync(this IOdooClient client, string model, OdooDomainBuilder domain, IReadOnlyDictionary<string, object?> values,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (domain.IsEmpty)
                throw new ArgumentException("Un domaine vide désignerait tous les enregistrements : précisez la clé de l'enregistrement.", nameof(domain));

            if (await client.FindIdAsync(model, domain, context, cancellationToken) is { } id)
            {
                await client.WriteAsync(model, [id], values, context, cancellationToken);
                return new OdooUpsertResult(id, Created: false);
            }

            return new OdooUpsertResult(await client.CreateAsync(model, values, context, cancellationToken), Created: true);
        }

        /// <summary>Deletes the records <paramref name="ids"/> (one call).</summary>
        public static Task DeleteAsync(this IOdooClient client, string model, IReadOnlyCollection<long> ids, OdooContext? context = null, CancellationToken cancellationToken = default)
            => client.UnlinkAsync(model, ids, context, cancellationToken);

        /// <summary>Deletes the records matching <paramref name="domain"/>, returns their number (0 is not an error).</summary>
        public static async Task<int> DeleteWhereAsync(this IOdooClient client, string model, OdooDomainBuilder domain, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (domain.IsEmpty)
                throw new ArgumentException("Un domaine vide supprimerait tous les enregistrements : utilisez DeleteAsync avec leurs identifiants.", nameof(domain));

            var ids = await client.SearchAsync(model, domain, null, "id", context, cancellationToken);
            await client.UnlinkAsync(model, ids, context, cancellationToken);
            return ids.Count;
        }

        /// <summary>Archives (active = false) or restores the records <paramref name="ids"/>.</summary>
        public static Task SetActiveAsync(this IOdooClient client, string model, IReadOnlyCollection<long> ids, bool active, OdooContext? context = null, CancellationToken cancellationToken = default)
            => client.WriteAsync(model, ids, new Dictionary<string, object?> { ["active"] = active }, context, cancellationToken);
    }

    /// <summary>Result of <see cref="OdooClientExtensions.UpsertAsync"/>.</summary>
    public sealed record OdooUpsertResult(long Id, bool Created);
}
