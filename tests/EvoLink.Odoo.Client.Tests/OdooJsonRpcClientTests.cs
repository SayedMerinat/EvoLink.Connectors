using System.Net;

using Newtonsoft.Json.Linq;


namespace EvoLink.Odoo.Client.Tests
{
    public class OdooJsonRpcClientTests
    {
        private const string Secret = "secret-key-123";

        private static OdooJsonRpcClient Client(FakeHttpHandler handler, string url = "https://odoo.test/", TimeSpan? timeout = null)
            => new(new OdooConnection { Url = url, Database = "odoo", Username = "ortolan", ApiKey = Secret, Timeout = timeout ?? TimeSpan.FromSeconds(30) }, handler);

        [Fact]
        public async Task SearchRead_AuthenticatesThenSendsTheExactJsonRpcRequests()
        {
            var handler = new FakeHttpHandler
            {
                Result = body => (string?)body["params"]!["method"] == "authenticate"
                    ? 7
                    : JArray.Parse("""[ { "id": 3, "name": "EvoLink SA", "parent_id": [1, "Groupe"], "email": false, "credit_limit": 12.5, "write_date": "2024-01-15 08:30:00" } ]"""),
            };

            using var client = Client(handler);
            var domain = JArray.Parse("""[["is_company", "=", true]]""");

            var records = await client.SearchReadAsync("res.partner", domain, ["id", "name"], 0, 2, "id", new OdooContext { Language = "fr_CH", ActiveTest = false });

            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, r => Assert.Equal("https://odoo.test/jsonrpc", r.Uri.ToString()));
            Assert.All(handler.Requests, r => Assert.Equal("application/json", r.MediaType));

            Assert.Equal("""{"jsonrpc":"2.0","method":"call","params":{"service":"common","method":"authenticate","args":["odoo","ortolan","secret-key-123",{}]},"id":1}""", handler.Requests[0].Body);
            Assert.Equal("""{"jsonrpc":"2.0","method":"call","params":{"service":"object","method":"execute_kw","args":["odoo",7,"secret-key-123","res.partner","search_read",[[["is_company","=",true]]],{"fields":["id","name"],"offset":0,"limit":2,"order":"id","context":{"lang":"fr_CH","active_test":false}}]},"id":2}""", handler.Requests[1].Body);

            var record = Assert.Single(records);
            Assert.Equal(3L, record["id"]);
            Assert.Equal(new List<object?> { 1L, "Groupe" }, record["parent_id"]);
            Assert.Equal(false, record["email"]);
            Assert.Equal(12.5, record["credit_limit"]);
            Assert.Equal("2024-01-15 08:30:00", record["write_date"]);
        }

        [Fact]
        public async Task Authentication_IsDoneOnceForTheClient()
        {
            var handler = new FakeHttpHandler { Result = body => (string?)body["params"]!["method"] == "authenticate" ? 7 : new JArray(1, 2) };
            using var client = Client(handler, url: "https://odoo.test/jsonrpc");

            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.SearchAsync("res.partner", [])));

            Assert.Single(handler.Bodies, b => (string?)b["params"]!["method"] == "authenticate");
            Assert.Equal(5, handler.ExecuteKwArgs.Count());
            Assert.All(handler.Requests, r => Assert.Equal("https://odoo.test/jsonrpc", r.Uri.ToString()));
        }

        [Fact]
        public async Task OrmMethods_SendTheExpectedArguments()
        {
            var handler = new FakeHttpHandler
            {
                Result = body => ((string?)body["params"]!["method"], body["params"]!["args"] is JArray { Count: > 4 } a ? (string?)a[4] : null) switch
                {
                    ("authenticate", _) => 7,
                    (_, "create") => new JArray(11, 12),
                    (_, "search") => new JArray(5),
                    (_, "name_search") => JArray.Parse("""[[4, "VIP"]]"""),
                    (_, "fields_get") => JObject.Parse("""{ "name": { "type": "char", "string": "Nom", "required": true }, "parent_id": { "type": "many2one", "relation": "res.partner", "readonly": false, "store": true }, "total": { "type": "monetary", "readonly": true, "store": false } }"""),
                    _ => true,
                },
            };

            using var client = Client(handler);
            var context = new OdooContext { Language = "de_CH" };

            var ids = await client.CreateAsync("res.partner", [new Dictionary<string, object?> { ["name"] = "A", ["parent_id"] = false }, new Dictionary<string, object?> { ["name"] = "B", ["credit_limit"] = 1.5m }], context);
            await client.WriteAsync("res.partner", [11, 12], new Dictionary<string, object?> { ["category_id"] = new List<object?> { new List<object?> { 6L, 0L, new List<object?> { 1L, 2L } } } }, context);
            await client.UnlinkAsync("res.partner", [12], context);
            var found = await client.SearchAsync("res.partner", JArray.Parse("""[["ref", "=", "C1"]]"""), 2, "id", context);
            var names = await client.NameSearchAsync("res.partner.category", "VIP", "=", null, 2, context);
            var fields = await client.FieldsGetAsync("res.partner");

            Assert.Equal([11L, 12L], ids);
            Assert.Equal([5L], found);
            Assert.Equal(new OdooNameResult(4, "VIP"), Assert.Single(names));

            Assert.Equal("char", fields["name"].Type);
            Assert.True(fields["name"].Required);
            Assert.Equal("Nom", fields["name"].Label);
            Assert.Equal("res.partner", fields["parent_id"].Relation);
            Assert.True(fields["total"].Readonly);
            Assert.False(fields["total"].Store);

            var calls = handler.ExecuteKwArgs.Select(a => a.Skip(3).Select(t => t.ToString(Newtonsoft.Json.Formatting.None))).Select(a => string.Join(" ", a)).ToList();

            Assert.Equal(
            [
                """ "res.partner" "create" [[{"name":"A","parent_id":false},{"name":"B","credit_limit":1.5}]] {"context":{"lang":"de_CH"}}""".Trim(),
                """ "res.partner" "write" [[11,12],{"category_id":[[6,0,[1,2]]]}] {"context":{"lang":"de_CH"}}""".Trim(),
                """ "res.partner" "unlink" [[12]] {"context":{"lang":"de_CH"}}""".Trim(),
                """ "res.partner" "search" [[["ref","=","C1"]]] {"limit":2,"order":"id","context":{"lang":"de_CH"}}""".Trim(),
                """ "res.partner.category" "name_search" [] {"name":"VIP","args":[],"operator":"=","limit":2,"context":{"lang":"de_CH"}}""".Trim(),
                """ "res.partner" "fields_get" [] {"attributes":["type","string","relation","readonly","store","required","selection"],"context":{}}""".Trim(),
            ], calls);
        }

        [Fact]
        public async Task RefusedAuthentication_IsAnUnavailabilityWithoutTheSecret()
        {
            var handler = new FakeHttpHandler { Result = _ => false };
            using var client = Client(handler);

            var ex = await Assert.ThrowsAsync<OdooUnavailableException>(() => client.SearchAsync("res.partner", []));

            Assert.Contains("Authentification refusée", ex.Message);
            Assert.DoesNotContain(Secret, ex.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task AccessDenied_IsAnUnavailability()
        {
            var handler = new FakeHttpHandler();
            handler.Respond = (body, _) => Task.FromResult((string?)body["params"]!["method"] == "authenticate"
                ? FakeHttpHandler.Json(new JObject { ["jsonrpc"] = "2.0", ["id"] = body["id"], ["result"] = 7 })
                : FakeHttpHandler.Error(body["id"]!, "odoo.exceptions.AccessDenied", "Access Denied"));

            using var client = Client(handler);

            await Assert.ThrowsAsync<OdooUnavailableException>(() => client.SearchAsync("res.partner", []));
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        public async Task ServerErrors_AreUnavailabilities(HttpStatusCode status)
        {
            var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html>maintenance</html>") }) };
            using var client = Client(handler);

            var ex = await Assert.ThrowsAsync<OdooUnavailableException>(() => client.SearchAsync("res.partner", []));
            Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
        }

        [Fact]
        public async Task NotFound_IsAConfigurationError()
        {
            var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) };
            using var client = Client(handler);

            await Assert.ThrowsAsync<OdooConfigurationException>(() => client.SearchAsync("res.partner", []));
        }

        [Fact]
        public async Task NetworkFailure_IsAnUnavailability()
        {
            var handler = new FakeHttpHandler { Respond = (_, _) => throw new HttpRequestException("Connexion refusée") };
            using var client = Client(handler);

            var ex = await Assert.ThrowsAsync<OdooUnavailableException>(() => client.SearchAsync("res.partner", []));
            Assert.IsType<HttpRequestException>(ex.InnerException);
        }

        [Fact]
        public async Task InvalidJson_IsAnUnavailability()
        {
            var handler = new FakeHttpHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy</html>") }) };
            using var client = Client(handler);

            await Assert.ThrowsAsync<OdooUnavailableException>(() => client.SearchAsync("res.partner", []));
        }

        [Fact]
        public async Task Timeout_IsAnUnavailabilityAndNotACancellation()
        {
            var handler = new FakeHttpHandler
            {
                Respond = async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                },
            };

            using var client = Client(handler, timeout: TimeSpan.FromMilliseconds(200));

            var ex = await Assert.ThrowsAsync<OdooUnavailableException>(() => client.SearchAsync("res.partner", []));
            Assert.IsType<TimeoutException>(ex.InnerException);
        }

        [Fact]
        public async Task CancellationOfTheJob_IsKeptAsACancellation()
        {
            var handler = new FakeHttpHandler
            {
                Respond = async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                },
            };

            using var client = Client(handler);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync("res.partner", [], cancellationToken: cancellation.Token));
        }

        [Fact]
        public async Task BusinessError_IsAnOdooRpcExceptionWithTheReadableMessage()
        {
            var handler = new FakeHttpHandler();
            handler.Respond = (body, _) => Task.FromResult((string?)body["params"]!["method"] == "authenticate"
                ? FakeHttpHandler.Json(new JObject { ["jsonrpc"] = "2.0", ["id"] = body["id"], ["result"] = 7 })
                : FakeHttpHandler.Error(body["id"]!, "odoo.exceptions.ValidationError", "Le champ « Nom » est obligatoire."));

            using var client = Client(handler);

            var ex = await Assert.ThrowsAsync<OdooRpcException>(() => client.CreateAsync("res.partner", [new Dictionary<string, object?> { ["ref"] = "C1" }]));

            Assert.Equal("Le champ « Nom » est obligatoire.", ex.OdooMessage);
            Assert.Equal("odoo.exceptions.ValidationError", ex.OdooName);
            Assert.Contains("'create' sur le modèle 'res.partner'", ex.Message);
            Assert.Contains("Le champ « Nom » est obligatoire.", ex.Message);
            Assert.DoesNotContain("Traceback", ex.Message);
            Assert.False(ex.IsAccessDenied);
        }

        [Fact]
        public async Task SearchReadAll_ReadsEveryPage()
        {
            var handler = new FakeHttpHandler
            {
                Result = body =>
                {
                    if ((string?)body["params"]!["method"] == "authenticate")
                        return 7;

                    var kwargs = (JObject)body["params"]!["args"]![6]!;
                    var offset = (int)kwargs["offset"]!;
                    var limit = (int)kwargs["limit"]!;

                    return new JArray(Enumerable.Range(1, 5).Skip(offset).Take(limit).Select(i => new JObject { ["id"] = i }));
                },
            };

            using var client = Client(handler);

            var records = await client.SearchReadAllAsync("res.partner", [], ["id"], pageSize: 2);

            Assert.Equal([1L, 2L, 3L, 4L, 5L], records.Select(r => (long)r["id"]!));
            Assert.Equal(3, handler.ExecuteKwArgs.Count());

            var limited = await client.SearchReadAllAsync("res.partner", [], ["id"], pageSize: 2, maxRecords: 3);
            Assert.Equal(3, limited.Count);
        }
    }
}
