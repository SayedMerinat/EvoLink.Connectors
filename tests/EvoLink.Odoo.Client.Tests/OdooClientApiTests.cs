using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EvoLink.Odoo.Client.Tests
{
    /// <summary>
    /// The API added on top of the ORM methods (domains, usual operations, typed reading, commands) and the errors of the former
    /// EvoLink.Odoo.API library that must not come back.
    /// </summary>
    public class OdooClientApiTests
    {
        private static OdooJsonRpcClient Client(FakeHttpHandler handler, string url = "https://odoo.test")
            => new(new OdooConnection { Url = url, Database = "odoo", Username = "ortolan", ApiKey = "secret" }, handler);

        /// <summary>Handler answering the execute_kw calls by ORM method (uid 7 for the authentication).</summary>
        private static FakeHttpHandler Odoo(Func<string, JArray, JObject, JToken> answer)
            => new()
            {
                Result = body =>
                {
                    if ((string?)body["params"]!["method"] == "authenticate")
                        return 7;

                    var args = (JArray)body["params"]!["args"]!;
                    return answer((string)args[4]!, (JArray)args[5], (JObject)args[6]);
                },
            };

        private static List<string> Methods(FakeHttpHandler handler)
            => handler.ExecuteKwArgs.Select(a => (string)a[4]!).ToList();

        // --- Domains -------------------------------------------------------------------------------------------------

        [Fact]
        public void Domain_BuildsThePrefixNotation()
        {
            var domain = OdooDomainBuilder.Where("is_company", "=", true)
                .And(OdooDomainBuilder.In("city", new[] { "Lausanne", "Genève" }).Or("country_id.code", "=", "CH"));

            Assert.Equal("""["&",["is_company","=",true],"|",["city","in",["Lausanne","Genève"]],["country_id.code","=","CH"]]""", domain.ToString());
        }

        [Fact]
        public void Domain_MakesAnImplicitAndExplicit_WhenItBecomesAnOperand()
        {
            // [a, b] means a AND b: as the left operand of a OR it must become ["&", a, b].
            var both = OdooDomainBuilder.Parse("""[["a", "=", 1], ["b", "=", 2]]""");

            Assert.Equal("""["|","&",["a","=",1],["b","=",2],["c","=",3]]""", both.Or("c", "=", 3).ToString());
            Assert.Equal("""["!","&",["a","=",1],["b","=",2]]""", OdooDomainBuilder.Not(both).ToString());
            Assert.Equal("""["|",["a","=",1],["b","=",2]]""", OdooDomainBuilder.Parse("""["|", ["a", "=", 1], ["b", "=", 2]]""").ToString());
        }

        [Fact]
        public void Domain_EmptyDomainsAreNeutral_AndInvalidOnesAreRejected()
        {
            Assert.True(OdooDomainBuilder.All.IsEmpty);
            Assert.Equal("""[["a","=",1]]""", OdooDomainBuilder.All.And("a", "=", 1).ToString());
            Assert.Equal("""["&",["a","=",1],["b","=",2]]""", OdooDomainBuilder.AllOf(OdooDomainBuilder.Equal("a", 1), OdooDomainBuilder.All, OdooDomainBuilder.Equal("b", 2)).ToString());

            Assert.Throws<FormatException>(() => OdooDomainBuilder.Parse("{ \"a\": 1 }"));
            Assert.Throws<FormatException>(() => OdooDomainBuilder.Parse("""[["a", "="]]"""));
            Assert.Throws<FormatException>(() => OdooDomainBuilder.Parse("""["or", ["a", "=", 1]]"""));
            Assert.Throws<ArgumentException>(() => OdooDomainBuilder.Where("", "=", 1));
        }

        // --- Usual operations ----------------------------------------------------------------------------------------

        [Fact]
        public async Task FindId_ReturnsNullWhenNothingMatches_AndRefusesAnAmbiguousSearch()
        {
            using var none = Client(Odoo((_, _, _) => new JArray()));
            Assert.Null(await none.FindIdAsync("res.partner", OdooDomainBuilder.Equal("ref", "X")));
            Assert.Null(await none.FindOneAsync("res.partner", OdooDomainBuilder.Equal("ref", "X")));
            Assert.Empty(await none.FindIdsAsync("res.partner", OdooDomainBuilder.Equal("ref", "X")));

            using var two = Client(Odoo((_, _, _) => new JArray(3, 4)));
            var exception = await Assert.ThrowsAsync<OdooAmbiguousResultException>(() => two.FindIdAsync("res.partner", OdooDomainBuilder.Equal("ref", "X")));
            Assert.Equal([3L, 4L], exception.Ids);
        }

        [Fact]
        public async Task Upsert_CreatesOnlyWhenTheSearchFoundNothing()
        {
            var created = Odoo((method, _, _) => method switch { "search" => new JArray(), "create" => new JArray(42), _ => true });
            using (var client = Client(created))
                Assert.Equal(new OdooUpsertResult(42, Created: true), await client.UpsertAsync("res.partner", OdooDomainBuilder.Equal("ref", "C1"), new Dictionary<string, object?> { ["name"] = "A" }));
            Assert.Equal(["search", "create"], Methods(created));

            var updated = Odoo((method, _, _) => method == "search" ? new JArray(9) : true);
            using (var client = Client(updated))
                Assert.Equal(new OdooUpsertResult(9, Created: false), await client.UpsertAsync("res.partner", OdooDomainBuilder.Equal("ref", "C1"), new Dictionary<string, object?> { ["name"] = "A" }));
            Assert.Equal(["search", "write"], Methods(updated));
        }

        [Fact]
        public async Task Upsert_NeverCreatesWhenTheSearchFails()
        {
            // The former library created the record whenever the search threw, even on a network failure: duplicates.
            var handler = Odoo((method, _, _) => method == "search" ? throw new InvalidOperationException("coupure") : new JArray(1));
            handler.Respond = (body, _) => (string?)body["params"]!["method"] == "authenticate"
                ? Task.FromResult(FakeHttpHandler.Json(new JObject { ["jsonrpc"] = "2.0", ["id"] = body["id"], ["result"] = 7 }))
                : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));

            using var client = Client(handler);

            await Assert.ThrowsAsync<OdooUnavailableException>(() => client.UpsertAsync("res.partner", OdooDomainBuilder.Equal("ref", "C1"), new Dictionary<string, object?> { ["name"] = "A" }));
            Assert.DoesNotContain("create", Methods(handler));

            using var ambiguous = Client(Odoo((method, _, _) => method == "search" ? new JArray(1, 2) : true));
            await Assert.ThrowsAsync<OdooAmbiguousResultException>(() => ambiguous.UpsertAsync("res.partner", OdooDomainBuilder.Equal("ref", "C1"), new Dictionary<string, object?>()));
            await Assert.ThrowsAsync<ArgumentException>(() => ambiguous.UpsertAsync("res.partner", OdooDomainBuilder.All, new Dictionary<string, object?>()));
        }

        [Fact]
        public async Task DeleteWhere_DeletingNothingIsNotAnError()
        {
            var handler = Odoo((method, _, _) => method == "search" ? new JArray() : true);
            using var client = Client(handler);

            Assert.Equal(0, await client.DeleteWhereAsync("res.partner", OdooDomainBuilder.Equal("ref", "X")));
            Assert.Equal(["search"], Methods(handler));
            await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteWhereAsync("res.partner", OdooDomainBuilder.All));
        }

        [Fact]
        public async Task Get_ThrowsNotFound_ReadKeepsTheOrderOfTheIds()
        {
            var handler = Odoo((method, args, _) => method == "search_read" && args[0].ToString(Formatting.None) == """[["id","in",[7]]]"""
                ? new JArray()
                : JArray.Parse("""[{ "id": 3, "name": "C" }, { "id": 1, "name": "A" }]"""));

            using var client = Client(handler);

            var missing = await Assert.ThrowsAsync<OdooRecordNotFoundException>(() => client.GetAsync("res.partner", 7));
            Assert.Equal(7, missing.Id);

            var records = await client.ReadAsync("res.partner", [1, 3, 99]);
            Assert.Equal(["A", "C"], records.Select(r => r.GetText("name")));
        }

        [Fact]
        public async Task Count_SetActive_AndCustomMethods()
        {
            var handler = Odoo((method, _, _) => method == "search_count" ? 12 : true);
            using var client = Client(handler);

            Assert.Equal(12, await client.CountAsync("res.partner", OdooDomainBuilder.Equal("active", true)));
            await client.SetActiveAsync("res.partner", [4, 5], active: false);
            await client.ExecuteKwAsync("sale.order", "action_confirm", [new JArray(8)], new JObject { ["context"] = new JObject { ["custom"] = 1 } }, new OdooContext { Language = "fr_CH" });

            var calls = handler.ExecuteKwArgs.Select(a => $"{a[4]} {a[5].ToString(Formatting.None)} {a[6].ToString(Formatting.None)}").ToList();
            Assert.Equal(
            [
                """search_count [[["active","=",true]]] {"context":{}}""",
                """write [[4,5],{"active":false}] {"context":{}}""",
                // A context given in the keyword arguments is kept.
                """action_confirm [[8]] {"context":{"custom":1}}""",
            ], calls);
        }

        // --- Values --------------------------------------------------------------------------------------------------

        [Fact]
        public void Records_AreReadWithTheirType_FalseBeingEmpty()
        {
            var record = OdooJson.ToRecord(JObject.Parse("""
                { "id": 3, "name": "EvoLink", "email": false, "parent_id": [1, "Groupe"], "user_id": false, "category_id": [4, 2],
                  "credit_limit": 1234567.89, "employees": 12, "birthday": "1990-05-17", "last_contact": "2024-01-15 08:30:00", "is_company": true }
                """));

            Assert.Equal("EvoLink", record.GetText("name"));
            Assert.Null(record.GetText("email"));
            Assert.Equal(1L, record.GetId("parent_id"));
            Assert.Equal("Groupe", record.GetReferenceName("parent_id"));
            Assert.Null(record.GetId("user_id"));
            Assert.Equal([4L, 2L], record.GetIds("category_id"));
            Assert.Equal(1234567.89m, record.GetDecimal("credit_limit"));
            Assert.Equal(12m, record.GetDecimal("employees"));
            Assert.Equal(new DateTime(1990, 5, 17), record.GetDate("birthday"));
            Assert.Equal(new DateTime(2024, 1, 15, 8, 30, 0, DateTimeKind.Utc), record.GetDateTimeUtc("last_contact"));
            Assert.Equal(DateTimeKind.Utc, record.GetDateTimeUtc("last_contact")!.Value.Kind);
            Assert.True(record.GetBool("is_company"));
            Assert.False(record.GetBool("absent"));

            var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
            Assert.Equal(new DateTime(2024, 1, 15, 9, 30, 0), record.GetDateTime("last_contact", zurich));

            Assert.Throws<FormatException>(() => record.GetId("name"));
            Assert.Throws<FormatException>(() => record.GetDate("name"));
        }

        [Fact]
        public void Numbers_KeepTheirPrecision()
        {
            // The former library converted the floats to float (32 bits): 1234567.89 was sent as 1234567.875.
            Assert.Equal("1234567.89", OdooJson.ToJson(1234567.89).ToString(Formatting.None));
            Assert.Equal("1234567.89", OdooJson.ToJson(1234567.89m).ToString(Formatting.None));
            Assert.Equal("12.5", OdooJson.ToJson(12.5f).ToString(Formatting.None));
            Assert.Equal(1234567.89, OdooJson.ToClr(JToken.Parse("1234567.89")));
            Assert.Equal(9007199254740993L, OdooJson.ToClr(JToken.Parse("9007199254740993")));
        }

        [Fact]
        public void Dates_AreSentInUtc()
        {
            var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

            Assert.Equal("2024-07-01", OdooFormats.Date(new DateTime(2024, 7, 1, 15, 0, 0)));
            Assert.Equal("2024-07-01 13:00:00", OdooFormats.DateTime(new DateTime(2024, 7, 1, 15, 0, 0, 500), zurich));
            Assert.Equal("2024-07-01 15:00:00", OdooFormats.DateTime(new DateTime(2024, 7, 1, 15, 0, 0, DateTimeKind.Utc)));
            Assert.Equal("2024-07-01 13:00:00", OdooFormats.DateTime(new DateTimeOffset(2024, 7, 1, 15, 0, 0, TimeSpan.FromHours(2))));
        }

        [Fact]
        public void Commands_OfTheRelationalFields()
        {
            string Json(object value) => OdooJson.ToJson(value).ToString(Formatting.None);

            Assert.Equal("[[6,0,[1,2]]]", Json(OdooCommands.Set([1, 2])));
            Assert.Equal("[[4,3],[4,4]]", Json(OdooCommands.Link([3, 4])));
            Assert.Equal("[[3,3]]", Json(OdooCommands.Unlink([3])));
            Assert.Equal("[[5]]", Json(OdooCommands.Clear()));
            Assert.Equal("""[[0,0,{"name":"Ligne"}]]""", Json(OdooCommands.Create([new Dictionary<string, object?> { ["name"] = "Ligne" }])));
        }

        // --- Connection and isolation --------------------------------------------------------------------------------

        [Fact]
        public void Connection_IsValidated_AndNeverShowsTheKey()
        {
            Assert.Throws<OdooConfigurationException>(() => new OdooJsonRpcClient("societe.odoo.com", "db", "user", "key"));
            Assert.Throws<OdooConfigurationException>(() => new OdooJsonRpcClient("https://societe.odoo.com", "db", "user", " "));
            Assert.Throws<OdooConfigurationException>(() => new OdooJsonRpcClient("https://societe.odoo.com", "db", "user", "key", TimeSpan.Zero));

            var connection = new OdooConnection { Url = "https://societe.odoo.com/", Database = "db", Username = "user", ApiKey = "super-secret" };
            Assert.DoesNotContain("super-secret", connection.ToString());
            Assert.Equal("https://societe.odoo.com/jsonrpc", connection.Endpoint.ToString());
        }

        [Fact]
        public async Task TwoClients_NeverShareTheirModels()
        {
            // The former library kept the fields of a model in a static cache keyed by its name only: a production and a test
            // Odoo mixed their fields, and a field added in Odoo was ignored until the process restarted.
            var production = Odoo((_, _, _) => JObject.Parse("""{ "name": { "type": "char" } }"""));
            var test = Odoo((_, _, _) => JObject.Parse("""{ "name": { "type": "char" }, "x_code": { "type": "char" } }"""));

            using var first = Client(production, "https://prod.test");
            using var second = Client(test, "https://test.test");

            Assert.Single(await first.FieldsGetAsync("res.partner"));
            Assert.Equal(2, (await second.FieldsGetAsync("res.partner")).Count);
            Assert.Single(await first.FieldsGetAsync("res.partner"));
        }

        [Fact]
        public async Task Context_SendsTheLanguageTheTimeZoneAndCustomKeys()
        {
            var handler = Odoo((_, _, _) => new JArray());
            using var client = Client(handler);

            var context = new OdooContext { Language = "fr_CH", TimeZone = "Europe/Zurich", Values = new Dictionary<string, object?> { ["allowed_company_ids"] = new[] { 1, 2 } } };
            await client.SearchAsync("res.partner", [], context: context.WithArchived());

            Assert.Equal("""{"context":{"allowed_company_ids":[1,2],"lang":"fr_CH","active_test":false,"tz":"Europe/Zurich"}}""", handler.ExecuteKwArgs.Single()[6].ToString(Formatting.None));
        }

        [Fact]
        public async Task SelectionValues_AreReadWithTheFields()
        {
            using var client = Client(Odoo((_, _, _) => JObject.Parse("""{ "type": { "type": "selection", "selection": [["contact", "Contact"], ["invoice", "Adresse de facturation"]] } }""")));

            var field = (await client.FieldsGetAsync("res.partner"))["type"];

            Assert.Equal(["contact", "invoice"], field.Selection!.Select(s => s.Key));
            Assert.True(field.IsWritable);
            Assert.False(new OdooFieldInfo("display_name", "char").IsWritable);
            Assert.False(new OdooFieldInfo("total", "monetary", Readonly: true, Store: false).IsWritable);
        }
    }
}
