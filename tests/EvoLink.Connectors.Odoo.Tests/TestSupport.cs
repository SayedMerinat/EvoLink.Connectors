using System.Collections.Concurrent;
using System.Net;
using System.Text;
using EvoLink.Connectors.Odoo;
using EvoLink.Odoo.Client;
using EvoLink.Logging.Classes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Logging;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;
using Ortolan.Core.Sync;

namespace EvoLink.Connectors.Odoo.Tests
{
    internal sealed class ListLogger : IOrtolanLogger
    {
        public ConcurrentQueue<(Severity Severity, string Message)> Entries { get; } = new();

        public IEnumerable<string> Warnings => Entries.Where(e => e.Severity == Severity.Warning).Select(e => e.Message);

        public void Log(Severity severity, string message, string? serviceId = null, string? jobId = null, string? category = null)
            => Entries.Enqueue((severity, message));
    }

    internal static class TestContexts
    {
        public static JobContext Create(SyncMode mode = SyncMode.Incremental, IOrtolanLogger? logger = null, DateTimeOffset? lastRun = null,
            List<string>? keyFields = null, bool deleteMissing = true, CancellationToken cancellationToken = default)
            => new()
            {
                JobId = Guid.NewGuid().ToString("N"),
                ServiceId = "service",
                Mode = mode,
                LastSuccessfulRun = lastRun,
                Sync = new SyncOptions { KeyFields = keyFields ?? ["ref"], DeleteMissing = deleteMissing },
                Logger = logger ?? NullOrtolanLogger.Instance,
                CancellationToken = cancellationToken,
            };

        public static List<Entity> Tracked(params Entity[] entities)
        {
            foreach (var entity in entities)
                entity.TrackingId = Guid.NewGuid().ToString("N");

            return [.. entities];
        }
    }

    /// <summary>
    /// In-memory Odoo behaving like the ORM through JSON-RPC: <c>false</c> for an empty field, <c>[id, "name"]</c> for a many2one,
    /// archived records hidden unless <c>active_test</c> is false, implicit AND of the domain leaves.
    /// </summary>
    internal sealed class FakeOdooClient : IOdooClient
    {
        private readonly Lock _lock = new();
        private long _nextId = 100;

        public Dictionary<string, Dictionary<string, OdooFieldInfo>> Models { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SortedDictionary<long, Dictionary<string, object?>>> Data { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public List<JArray> Domains { get; } = [];
        public List<OdooContext?> Contexts { get; } = [];
        public List<(int Offset, int Limit)> Pages { get; } = [];

        /// <summary>Exception thrown by every call (unavailability simulation).</summary>
        public Exception? FailWith { get; set; }

        /// <summary>Exception thrown by the calls it returns one for (the call is described as "method model ...").</summary>
        public Func<string, Exception?>? FailOnCall { get; set; }

        /// <summary>Business error of a creation (e.g. a required field).</summary>
        public Func<IReadOnlyDictionary<string, object?>, Exception?>? CreateError { get; set; }

        public int Disposed { get; private set; }

        public static FakeOdooClient Partners()
        {
            var client = new FakeOdooClient();

            client.Define("res.partner",
                new OdooFieldInfo("id", "integer", Readonly: true),
                new OdooFieldInfo("name", "char", Required: true),
                new OdooFieldInfo("ref", "char"),
                new OdooFieldInfo("email", "char"),
                new OdooFieldInfo("active", "boolean"),
                new OdooFieldInfo("is_company", "boolean"),
                new OdooFieldInfo("credit_limit", "float"),
                new OdooFieldInfo("employees", "integer"),
                new OdooFieldInfo("birthday", "date"),
                new OdooFieldInfo("last_contact", "datetime"),
                new OdooFieldInfo("parent_id", "many2one", "res.partner"),
                new OdooFieldInfo("category_id", "many2many", "res.partner.category"),
                new OdooFieldInfo("child_ids", "one2many", "res.partner"),
                new OdooFieldInfo("display_name", "char", Readonly: true, Store: false),
                new OdooFieldInfo("total_due", "monetary", Readonly: true, Store: false),
                new OdooFieldInfo("write_date", "datetime", Readonly: true));

            client.Define("res.partner.category",
                new OdooFieldInfo("id", "integer", Readonly: true),
                new OdooFieldInfo("name", "char"));

            client.Seed("res.partner.category", new() { ["name"] = "VIP" }, id: 1);
            client.Seed("res.partner.category", new() { ["name"] = "Fournisseur" }, id: 2);

            return client;
        }

        public void Define(string model, params OdooFieldInfo[] fields)
        {
            Models[model] = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            Data[model] = [];
        }

        /// <summary>Adds a record with the values of the ORM (many2one = id, many2many = list of ids, null = empty).</summary>
        public long Seed(string model, Dictionary<string, object?> values, long? id = null)
        {
            lock (_lock)
            {
                var recordId = id ?? _nextId++;
                var record = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = recordId };

                if (Models[model].ContainsKey("active"))
                    record["active"] = true;

                if (Models[model].ContainsKey("is_company"))
                    record["is_company"] = false;

                foreach (var (name, value) in values)
                    record[name] = value;

                Data[model][recordId] = record;
                return recordId;
            }
        }

        public Dictionary<string, object?> Record(string model, long id) => Data[model][id];

        public Task<IReadOnlyDictionary<string, OdooFieldInfo>> FieldsGetAsync(string model, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"fields_get {model}", context);
            return Task.FromResult<IReadOnlyDictionary<string, OdooFieldInfo>>(Models.TryGetValue(model, out var fields) ? fields : []);
        }

        public Task<IReadOnlyList<Dictionary<string, object?>>> SearchReadAsync(string model, JArray domain, IReadOnlyCollection<string>? fields, int offset, int limit,
            string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"search_read {model}", context, domain);

            lock (_lock)
            {
                Pages.Add((offset, limit));

                var columns = fields is { Count: > 0 } ? fields.ToList() : Models[model].Keys.ToList();
                var records = Filter(model, domain, context).Skip(offset).Take(limit > 0 ? limit : int.MaxValue)
                    .Select(r => columns.Append("id").Distinct().ToDictionary(c => c, c => ToOdoo(model, c, r.GetValueOrDefault(c)), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(records);
            }
        }

        public Task<IReadOnlyList<long>> SearchAsync(string model, JArray domain, int? limit = null, string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"search {model}", context, domain);

            lock (_lock)
                return Task.FromResult<IReadOnlyList<long>>(Filter(model, domain, context).Select(r => (long)r["id"]!).Take(limit ?? int.MaxValue).ToList());
        }

        public Task<IReadOnlyList<long>> CreateAsync(string model, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"create {model} {string.Join(",", records.SelectMany(r => r.Keys).Distinct().Order())}", context);

            var ids = new List<long>();

            foreach (var values in records)
            {
                if (CreateError?.Invoke(values) is { } error)
                    throw error;

                ids.Add(Seed(model, FromPayload(model, values)));
            }

            return Task.FromResult<IReadOnlyList<long>>(ids);
        }

        public Task WriteAsync(string model, IReadOnlyCollection<long> ids, IReadOnlyDictionary<string, object?> values, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"write {model} {string.Join(",", ids)} {string.Join(",", values.Keys.Order())}", context);

            lock (_lock)
            {
                foreach (var id in ids)
                    foreach (var (name, value) in FromPayload(model, values))
                        Data[model][id][name] = value;
            }

            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string model, IReadOnlyCollection<long> ids, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"unlink {model} {string.Join(",", ids)}", context);

            lock (_lock)
            {
                foreach (var id in ids)
                    Data[model].Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OdooNameResult>> NameSearchAsync(string model, string name, string @operator = "ilike", JArray? domain = null, int limit = 100,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"name_search {model} {@operator} {name}", context);

            lock (_lock)
            {
                var comparison = @operator == "=" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var results = Data[model].Values
                    .Where(r => r.GetValueOrDefault("active") is not false)
                    .Where(r => @operator.Contains("ilike") && !@operator.StartsWith('=')
                        ? ((string?)r.GetValueOrDefault("name") ?? string.Empty).Contains(name, comparison)
                        : string.Equals((string?)r.GetValueOrDefault("name"), name, comparison))
                    .Take(limit)
                    .Select(r => new OdooNameResult((long)r["id"]!, (string?)r["name"] ?? string.Empty))
                    .ToList();

                return Task.FromResult<IReadOnlyList<OdooNameResult>>(results);
            }
        }

        public Task<JToken> ExecuteKwAsync(string model, string method, JArray args, JObject? kwargs = null, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            Enter($"{method} {model}", context, args.FirstOrDefault() as JArray);

            lock (_lock)
            {
                return method switch
                {
                    "search_count" => Task.FromResult<JToken>(Filter(model, (JArray?)args.FirstOrDefault() ?? [], context).Count()),
                    _ => throw new NotSupportedException($"Méthode Odoo '{method}' non simulée."),
                };
            }
        }

        public void Dispose()
            => Disposed++;

        private void Enter(string call, OdooContext? context, JArray? domain = null)
        {
            lock (_lock)
            {
                Calls.Add(call);
                Contexts.Add(context);

                if (domain != null)
                    Domains.Add((JArray)domain.DeepClone());
            }

            if (FailWith != null)
                throw FailWith;

            if (FailOnCall?.Invoke(call) is { } error)
                throw error;
        }

        private IEnumerable<Dictionary<string, object?>> Filter(string model, JArray domain, OdooContext? context)
        {
            var mentionsActive = domain.OfType<JArray>().Any(l => (string?)l[0] == "active");
            var activeTest = context?.ActiveTest != false && Models[model].ContainsKey("active") && !mentionsActive;

            return Data[model].Values
                .Where(r => !activeTest || r.GetValueOrDefault("active") is not false)
                .Where(r => domain.All(item => item is not JArray leaf || Matches(r.GetValueOrDefault((string)leaf[0]!), (string)leaf[1]!, ValueNormalizer.Normalize(leaf[2]))))
                .ToList();
        }

        private static bool Matches(object? actual, string @operator, object? expected)
        {
            static bool Equal(object? a, object? b)
                => b is false ? a is null or false : SyncComparer.Canonical(a) == SyncComparer.Canonical(b);

            int Compare()
                => actual is IConvertible && expected is long or double or decimal
                    ? Convert.ToDecimal(actual).CompareTo(Convert.ToDecimal(expected))
                    : string.CompareOrdinal(SyncComparer.Canonical(actual), SyncComparer.Canonical(expected));

            return @operator switch
            {
                "=" => Equal(actual, expected),
                "!=" => !Equal(actual, expected),
                "=ilike" => string.Equals(SyncComparer.Canonical(actual), SyncComparer.Canonical(expected), StringComparison.OrdinalIgnoreCase),
                ">" => actual != null && Compare() > 0,
                ">=" => actual != null && Compare() >= 0,
                "<" => actual != null && Compare() < 0,
                "<=" => actual != null && Compare() <= 0,
                "in" => expected is IEnumerable<object?> values && values.Any(v => Equal(actual, v)),
                _ => throw new NotSupportedException($"Opérateur '{@operator}' non simulé."),
            };
        }

        private object? ToOdoo(string model, string field, object? value)
        {
            var type = Models[model].TryGetValue(field, out var info) ? info.Type : "char";

            return type switch
            {
                "boolean" => value ?? false,
                "integer" => value is null ? 0L : Convert.ToInt64(value),
                "float" or "monetary" => value is null ? 0.0 : Convert.ToDouble(value),
                "many2one" => value is long id ? new List<object?> { id, NameOf(info!.Relation!, id) } : false,
                "many2many" or "one2many" => value is IEnumerable<long> ids ? ids.Cast<object?>().ToList() : new List<object?>(),
                _ => value is null or "" ? false : value,
            };
        }

        private Dictionary<string, object?> FromPayload(string model, IReadOnlyDictionary<string, object?> values)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var (name, value) in values)
            {
                if (!Models[model].TryGetValue(name, out var info) || name == "id")
                    throw new OdooRpcException($"Odoo a refusé l'opération : champ invalide '{name}'", $"Invalid field '{name}' on model '{model}'", "ValueError");

                result[name] = info.Type switch
                {
                    "boolean" => value,
                    "many2many" => ((IEnumerable<object?>)((IList<object?>)((IList<object?>)value!)[0]!)[2]!).Select(Convert.ToInt64).ToList(),
                    _ => value is false ? null : value,
                };
            }

            return result;
        }

        private string NameOf(string model, long id)
            => Data.TryGetValue(model, out var records) && records.TryGetValue(id, out var record) ? (string?)record.GetValueOrDefault("name") ?? $"#{id}" : $"#{id}";
    }

    internal sealed class TestOdooSynchroniser(FakeOdooClient client) : OdooSynchroniser
    {
        protected override IOdooClient CreateClient(OdooConnection connection) => client;
    }

    internal sealed class TestOdooProvider(FakeOdooClient client) : OdooProvider
    {
        protected override IOdooClient CreateClient(OdooConnection connection) => client;
    }

    internal sealed class TestOdooIdLookup(FakeOdooClient client) : OdooIdLookupTransformer
    {
        protected override IOdooClient CreateClient(OdooConnection connection) => client;
    }
}
