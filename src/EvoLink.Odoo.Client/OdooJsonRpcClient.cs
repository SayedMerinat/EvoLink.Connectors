using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Odoo 17 client over the external JSON-RPC API: <c>POST {url}/jsonrpc</c>, <c>common.authenticate</c> (database, user,
    /// API key) to get the uid, then <c>object.execute_kw</c>.
    /// <list type="bullet">
    /// <item>Thread-safe: one instance can serve parallel calls; the authentication is done once.</item>
    /// <item>No shared state: two instances (production and test Odoo) never mix their data.</item>
    /// <item>The API key is only sent in the request bodies, never logged nor included in an exception message.</item>
    /// </list>
    /// </summary>
    public sealed class OdooJsonRpcClient : IOdooClient
    {
        private static readonly string[] FieldAttributes = ["type", "string", "relation", "readonly", "store", "required", "selection"];

        private readonly OdooConnection _connection;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _authenticationLock = new(1, 1);
        private long _requestId;
        private long _uid;

        /// <param name="connection">Odoo instance.</param>
        /// <param name="handler">HTTP handler (tests, proxy). It is not disposed with the client.</param>
        /// <exception cref="OdooConfigurationException">Incomplete or invalid settings.</exception>
        public OdooJsonRpcClient(OdooConnection connection, HttpMessageHandler? handler = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _connection.Validate();

            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = connection.Timeout;
        }

        /// <summary>Client of the instance at <paramref name="url"/>, e.g. https://societe.odoo.com.</summary>
        public OdooJsonRpcClient(string url, string database, string username, string apiKey, TimeSpan? timeout = null)
            : this(new OdooConnection { Url = url, Database = database, Username = username, ApiKey = apiKey, Timeout = timeout ?? TimeSpan.FromSeconds(100) })
        {
        }

        /// <summary>
        /// <c>common.authenticate</c>: uid of the user, obtained once per client. A refused authentication is an unavailability.
        /// </summary>
        public async Task<long> AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            var uid = Interlocked.Read(ref _uid);
            if (uid > 0)
                return uid;

            await _authenticationLock.WaitAsync(cancellationToken);

            try
            {
                uid = Interlocked.Read(ref _uid);
                if (uid > 0)
                    return uid;

                JToken result;

                try
                {
                    result = await CallAsync("common", "authenticate",
                        new JArray(_connection.Database, _connection.Username, _connection.ApiKey, new JObject()), "'authenticate'", cancellationToken);
                }
                catch (OdooRpcException ex)
                {
                    throw new OdooUnavailableException($"Authentification impossible sur Odoo {_connection} : {ex.OdooMessage}", ex);
                }

                // Odoo answers false (not an error) when the credentials are refused.
                if (result.Type != JTokenType.Integer || result.Value<long>() <= 0)
                    throw new OdooUnavailableException($"Authentification refusée par Odoo {_connection} pour l'utilisateur '{_connection.Username}' : vérifiez la base, l'utilisateur et la clé API.");

                uid = result.Value<long>();
                Interlocked.Exchange(ref _uid, uid);

                return uid;
            }
            finally
            {
                _authenticationLock.Release();
            }
        }

        public async Task<JToken> ExecuteKwAsync(string model, string method, JArray args, JObject? kwargs = null, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Le modèle Odoo est obligatoire.", nameof(model));

            kwargs = kwargs == null ? [] : (JObject)kwargs.DeepClone();
            if (kwargs["context"] == null)
                kwargs["context"] = (context ?? OdooContext.Default).ToJson();

            var uid = await AuthenticateAsync(cancellationToken);
            var parameters = new JArray(_connection.Database, uid, _connection.ApiKey, model, method, args, kwargs);

            try
            {
                return await CallAsync("object", "execute_kw", parameters, $"'{method}' sur le modèle '{model}'", cancellationToken);
            }
            catch (OdooRpcException ex) when (ex.IsAccessDenied)
            {
                throw new OdooUnavailableException($"Accès refusé par Odoo {_connection} : {ex.OdooMessage}", ex);
            }
        }

        public async Task<IReadOnlyDictionary<string, OdooFieldInfo>> FieldsGetAsync(string model, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var result = await ExecuteKwAsync(model, "fields_get", [], new JObject { ["attributes"] = new JArray(FieldAttributes) }, context, cancellationToken);
            var fields = new Dictionary<string, OdooFieldInfo>(StringComparer.Ordinal);

            if (result is not JObject definitions)
                return fields;

            foreach (var property in definitions.Properties())
            {
                if (property.Value is not JObject definition)
                    continue;

                fields[property.Name] = new OdooFieldInfo(
                    property.Name,
                    Text(definition["type"]) ?? "char",
                    Text(definition["relation"]),
                    Flag(definition["readonly"], false),
                    Flag(definition["store"], true),
                    Flag(definition["required"], false),
                    Text(definition["string"]),
                    Selection(definition["selection"]));
            }

            return fields;
        }

        public async Task<IReadOnlyList<Dictionary<string, object?>>> SearchReadAsync(string model, JArray domain, IReadOnlyCollection<string>? fields, int offset, int limit,
            string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var kwargs = new JObject();

            if (fields is { Count: > 0 })
                kwargs["fields"] = new JArray(fields);

            kwargs["offset"] = Math.Max(0, offset);

            if (limit > 0)
                kwargs["limit"] = limit;

            if (!string.IsNullOrWhiteSpace(order))
                kwargs["order"] = order;

            var result = await ExecuteKwAsync(model, "search_read", [domain], kwargs, context, cancellationToken);

            return result is JArray records ? records.OfType<JObject>().Select(OdooJson.ToRecord).ToList() : [];
        }

        public async Task<IReadOnlyList<long>> SearchAsync(string model, JArray domain, int? limit = null, string? order = null, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var kwargs = new JObject();

            if (limit is > 0)
                kwargs["limit"] = limit.Value;

            if (!string.IsNullOrWhiteSpace(order))
                kwargs["order"] = order;

            return Ids(await ExecuteKwAsync(model, "search", [domain], kwargs, context, cancellationToken));
        }

        public async Task<IReadOnlyList<long>> CreateAsync(string model, IReadOnlyList<IReadOnlyDictionary<string, object?>> records, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (records.Count == 0)
                return [];

            var values = new JArray(records.Select(r => OdooJson.ToJson(r)));
            var ids = Ids(await ExecuteKwAsync(model, "create", [values], null, context, cancellationToken));

            if (ids.Count != records.Count || ids.Any(id => id <= 0))
                throw new OdooRpcException($"Odoo a renvoyé {ids.Count} identifiant(s) pour {records.Count} enregistrement(s) créé(s) dans le modèle '{model}'.", "réponse inattendue");

            return ids;
        }

        public async Task WriteAsync(string model, IReadOnlyCollection<long> ids, IReadOnlyDictionary<string, object?> values, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (ids.Count == 0 || values.Count == 0)
                return;

            await ExecuteKwAsync(model, "write", [new JArray(ids), OdooJson.ToJson(values)], null, context, cancellationToken);
        }

        public async Task UnlinkAsync(string model, IReadOnlyCollection<long> ids, OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            if (ids.Count == 0)
                return;

            await ExecuteKwAsync(model, "unlink", [new JArray(ids)], null, context, cancellationToken);
        }

        public async Task<IReadOnlyList<OdooNameResult>> NameSearchAsync(string model, string name, string @operator = "ilike", JArray? domain = null, int limit = 100,
            OdooContext? context = null, CancellationToken cancellationToken = default)
        {
            var kwargs = new JObject
            {
                ["name"] = name,
                ["args"] = domain ?? [],
                ["operator"] = string.IsNullOrWhiteSpace(@operator) ? "ilike" : @operator,
                ["limit"] = limit,
            };

            var result = await ExecuteKwAsync(model, "name_search", [], kwargs, context, cancellationToken);

            return result is JArray pairs
                ? pairs.OfType<JArray>().Where(p => p.Count >= 1 && p[0].Type == JTokenType.Integer)
                    .Select(p => new OdooNameResult(p[0].Value<long>(), p.Count > 1 ? Text(p[1]) ?? string.Empty : string.Empty)).ToList()
                : [];
        }

        public void Dispose()
        {
            _http.Dispose();
            _authenticationLock.Dispose();
        }

        private async Task<JToken> CallAsync(string service, string method, JArray args, string operation, CancellationToken cancellationToken)
        {
            var body = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "call",
                ["params"] = new JObject { ["service"] = service, ["method"] = method, ["args"] = args },
                ["id"] = Interlocked.Increment(ref _requestId),
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _connection.Endpoint)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            HttpResponseMessage response;
            string content;

            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OdooUnavailableException($"Odoo {_connection} n'a pas répondu dans le délai de {_http.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.", new TimeoutException(ex.Message, ex));
            }
            catch (HttpRequestException ex)
            {
                throw new OdooUnavailableException($"Odoo {_connection} est injoignable : {ex.Message}", ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new OdooUnavailableException($"Odoo {_connection} a répondu {status} {response.ReasonPhrase}.",
                        new HttpRequestException($"HTTP {status} {response.ReasonPhrase}", null, response.StatusCode));

                if (!response.IsSuccessStatusCode)
                    throw new OdooConfigurationException($"Odoo {_connection} a répondu {status} {response.ReasonPhrase} à l'adresse '{_connection.Endpoint}' : vérifiez l'URL de l'instance.");

                try
                {
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new OdooUnavailableException($"Odoo {_connection} n'a pas répondu dans le délai imparti.", new TimeoutException(ex.Message, ex));
                }
                catch (HttpRequestException ex)
                {
                    throw new OdooUnavailableException($"Odoo {_connection} est injoignable : {ex.Message}", ex);
                }
            }

            JObject json;

            try
            {
                using var reader = new JsonTextReader(new StringReader(content)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
                json = JObject.Load(reader);
            }
            catch (JsonException ex)
            {
                throw new OdooUnavailableException($"Odoo {_connection} a renvoyé une réponse qui n'est pas du JSON-RPC (instance en maintenance ou URL erronée ?).", ex);
            }

            if (json["error"] is JObject error)
                throw ToException(error, operation);

            // A null or false result is a valid answer (e.g. a write returns true, a refused authenticate false).
            return json["result"] ?? JValue.CreateNull();
        }

        private static OdooRpcException ToException(JObject error, string operation)
        {
            var data = error["data"] as JObject;
            var odooMessage = Text(data?["message"]) ?? Text(error["message"]) ?? "erreur inconnue";
            var code = error["code"]?.Type == JTokenType.Integer ? error["code"]!.Value<int>() : 0;

            return new OdooRpcException($"Odoo a refusé l'opération {operation} : {odooMessage}", odooMessage, Text(data?["name"]), code, Text(data?["debug"]));
        }

        private static List<long> Ids(JToken result)
            => result switch
            {
                JArray array => array.Where(t => t.Type == JTokenType.Integer).Select(t => t.Value<long>()).ToList(),
                { Type: JTokenType.Integer } => [result.Value<long>()],
                _ => [],
            };

        private static bool Flag(JToken? token, bool fallback)
            => token?.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;

        private static List<KeyValuePair<string, string>>? Selection(JToken? token)
            => token is JArray options
                ? options.OfType<JArray>().Where(o => o.Count >= 1).Select(o => new KeyValuePair<string, string>(Text(o[0]) ?? string.Empty, o.Count > 1 ? Text(o[1]) ?? string.Empty : string.Empty)).ToList()
                : null;

        private static string? Text(JToken? token)
            => token is null || token.Type is JTokenType.Null or JTokenType.Undefined || (token.Type == JTokenType.Boolean && !token.Value<bool>())
                ? null
                : token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
    }
}
