using EvoLink.Odoo.Client;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Odoo.Tests
{
    /// <summary>
    /// Options reproducing the OdooToolbox tools (odootosql / sqltoodoo): relations in their text format, null values that never
    /// empty an Odoo field, retried reads. Without these options the connectors behave as before.
    /// </summary>
    public class OdooToolboxCompatibilityTests
    {
        private static FakeOdooClient Odoo()
        {
            var client = FakeOdooClient.Partners();
            client.Seed("res.partner", new() { ["name"] = "EvoLink SA", ["ref"] = "P7", ["is_company"] = true }, id: 7);
            client.Seed("res.partner", new() { ["name"] = "Alice", ["ref"] = "C1", ["email"] = "alice@example.ch", ["employees"] = 12L, ["parent_id"] = 7L, ["category_id"] = new List<long> { 2, 1 } }, id: 10);
            client.Seed("res.partner", new() { ["name"] = "Bob", ["ref"] = "C2" }, id: 11);
            return client;
        }

        private static TestOdooProvider Provider(FakeOdooClient client, Action<OdooProvider>? configure = null)
        {
            var provider = new TestOdooProvider(client)
            {
                Url = "https://odoo.test",
                Database = "odoo",
                Username = "ortolan",
                ApiKey = "key",
                Model = "res.partner",
                TimeZone = "UTC",
            };

            configure?.Invoke(provider);
            return provider;
        }

        private static TestOdooSynchroniser Synchroniser(FakeOdooClient client, Action<OdooSynchroniser>? configure = null)
        {
            var synchroniser = new TestOdooSynchroniser(client)
            {
                Url = "https://odoo.test",
                Database = "odoo",
                Username = "ortolan",
                ApiKey = "key",
                Model = "res.partner",
                TimeZone = "UTC",
                Filters = [new OdooDomainFilter { Field = "is_company", Operator = "=", Value = false }],
            };

            configure?.Invoke(synchroniser);
            return synchroniser;
        }

        // ---------------------------------------------------------------- Relations
        [Fact]
        public async Task Provider_OdooToolboxFormat_WritesTheRelationsAsOdootosql()
        {
            var output = await Provider(Odoo(), p => p.RelationFormat = OdooRelationFormat.OdooToolbox).ActAsync([], TestContexts.Create());

            var alice = output.Single(e => e.GetString("ref") == "C1");
            var bob = output.Single(e => e.GetString("ref") == "C2");

            Assert.Equal("[7, 'EvoLink SA']", alice["parent_id"]);
            Assert.Equal("[2, 1]", alice["category_id"]);
            Assert.Null(bob["parent_id"]);
            Assert.Null(bob["category_id"]);
            Assert.Null(bob["child_ids"]);

            // The other values keep their usual conversion, and the identifier stays in the metadata.
            Assert.Equal(12L, alice["employees"]);
            Assert.Equal("10", alice.Metadata[OdooSynchroniser.IdMetadataKey]);
        }

        [Fact]
        public async Task Provider_DefaultFormat_IsUnchanged()
        {
            var alice = (await Provider(Odoo()).ActAsync([], TestContexts.Create())).Single(e => e.GetString("ref") == "C1");

            Assert.Equal(7L, alice["parent_id"]);
            Assert.Equal(new List<object?> { 1L, 2L }, alice["category_id"]);
        }

        [Theory]
        [InlineData("Société", "'Société'")]
        [InlineData("L'été", "\"L'été\"")]
        [InlineData("L'\"x\"", "'L\\'\"x\"'")]
        [InlineData("a\\b\nc", "'a\\\\b\\nc'")]
        [InlineData("prix net", "'prix\\xa0net'")]
        [InlineData("😀 Привет €", "'😀 Привет €'")]
        public void Repr_IsThePythonRepresentation(string text, string expected)
            => Assert.Equal(expected, OdooToolboxFormat.ReprString(text));

        [Theory]
        [InlineData("[5, 'Société']", 5L)]
        [InlineData("[5, \"L'été\"]", 5L)]
        [InlineData("[5]", 5L)]
        [InlineData("(5,)", 5L)]
        [InlineData(" [ 12 , 'x' ] ", 12L)]
        [InlineData("[A001] Chaise", null)]
        [InlineData("5", null)]
        [InlineData("Société", null)]
        public void ParseMany2One_ReadsOnlyTheOdooToolboxFormat(string text, long? expected)
            => Assert.Equal(expected, OdooToolboxFormat.ParseMany2One(text));

        [Fact]
        public async Task Synchroniser_OdooToolboxFormat_ReadsTheRelationsOfSqltoodoo()
        {
            var client = Odoo();
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C3", ["name"] = "Carla", ["parent_id"] = "[7, 'EvoLink SA']", ["category_id"] = "[1, 2]" });

            var output = await Synchroniser(client, s => s.RelationFormat = OdooRelationFormat.OdooToolbox)
                .ActAsync(source, TestContexts.Create(deleteMissing: false));

            Assert.Equal(SyncResult.Created, output[0].SyncResult);
            var id = long.Parse(output[0].Metadata[OdooSynchroniser.IdMetadataKey]);
            Assert.Equal(7L, client.Record("res.partner", id)["parent_id"]);
            Assert.Equal(new List<long> { 1, 2 }, ((IEnumerable<long>)client.Record("res.partner", id)["category_id"]!).Order().ToList());

            // The entity leaving the module keeps its original text.
            Assert.Equal("[7, 'EvoLink SA']", output[0]["parent_id"]);
        }

        [Fact]
        public async Task Synchroniser_DefaultFormat_StillRefusesTheText()
        {
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C3", ["name"] = "Carla", ["parent_id"] = "[7, 'EvoLink SA']" });

            var output = await Synchroniser(Odoo()).ActAsync(source, TestContexts.Create(deleteMissing: false));

            Assert.True(output[0].IsFailed);
        }

        // ---------------------------------------------------------------- Null values
        [Fact]
        public async Task SkipNullValues_NeverEmptiesAnOdooField()
        {
            var client = Odoo();
            var source = TestContexts.Tracked(
                new Entity { ["ref"] = "C1", ["name"] = "Alice", ["email"] = null, ["employees"] = null },
                new Entity { ["ref"] = "C2", ["name"] = "Bob B.", ["email"] = null });

            var output = await Synchroniser(client, s => s.SkipNullValues = true).ActAsync(source, TestContexts.Create(deleteMissing: false));

            Assert.Equal(SyncResult.Unchanged, output.Single(e => e.GetString("ref") == "C1").SyncResult);
            Assert.Equal(SyncResult.Updated, output.Single(e => e.GetString("ref") == "C2").SyncResult);
            Assert.Equal("alice@example.ch", client.Record("res.partner", 10)["email"]);
            Assert.Equal(12L, client.Record("res.partner", 10)["employees"]);
            Assert.Equal("Bob B.", client.Record("res.partner", 11)["name"]);

            // The entities leaving the module keep their null values.
            Assert.Null(output.Single(e => e.GetString("ref") == "C1")["email"]);
        }

        [Fact]
        public async Task SkipNullValues_LetsOdooApplyItsDefaultsOnCreation()
        {
            var client = Odoo();
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C3", ["name"] = "Carla", ["email"] = null, ["is_company"] = null });

            var output = await Synchroniser(client, s => s.SkipNullValues = true).ActAsync(source, TestContexts.Create(deleteMissing: false));

            var created = client.Record("res.partner", long.Parse(output[0].Metadata[OdooSynchroniser.IdMetadataKey]));
            Assert.False(created.ContainsKey("email"));
            Assert.Equal(false, created["is_company"]);
        }

        [Fact]
        public async Task WithoutSkipNullValues_ANullStillEmptiesTheField()
        {
            var client = Odoo();
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C1", ["name"] = "Alice", ["email"] = null });

            var output = await Synchroniser(client).ActAsync(source, TestContexts.Create(deleteMissing: false));

            Assert.Equal(SyncResult.Updated, output[0].SyncResult);
            Assert.Null(client.Record("res.partner", 10)["email"]);
        }

        // ---------------------------------------------------------------- Html
        [Theory]
        [InlineData("<p>Société &amp; Cie</p>", "Société & Cie")]
        [InlineData("<p>ligne 1<br>ligne 2</p>", "ligne 1\nligne 2")]
        [InlineData("<p>a</p><p>b</p>", "<p>a</p><p>b</p>")]
        [InlineData("<p><b>gras</b></p>", "<p><b>gras</b></p>")]
        [InlineData("texte", "texte")]
        public void HtmlComparable_ComparesASingleParagraphAsItsText(string html, string expected)
            => Assert.Equal(expected, OdooValues.HtmlComparable(html));

        [Fact]
        public async Task HtmlField_StoredAsAParagraphByOdoo_IsNotRewrittenAtEveryRun()
        {
            var client = Odoo();
            client.Models["res.partner"]["comment"] = new OdooFieldInfo("comment", "html");
            client.Record("res.partner", 10)["comment"] = "<p>Note &amp; suivi</p>";

            var unchanged = await Synchroniser(client).ActAsync(
                TestContexts.Tracked(new Entity { ["ref"] = "C1", ["name"] = "Alice", ["comment"] = "Note & suivi" }), TestContexts.Create(deleteMissing: false));
            var changed = await Synchroniser(client).ActAsync(
                TestContexts.Tracked(new Entity { ["ref"] = "C1", ["name"] = "Alice", ["comment"] = "Autre note" }), TestContexts.Create(deleteMissing: false));

            Assert.Equal(SyncResult.Unchanged, unchanged[0].SyncResult);
            Assert.Equal(SyncResult.Updated, changed[0].SyncResult);
            Assert.Equal("Autre note", client.Record("res.partner", 10)["comment"]);
        }

        // ---------------------------------------------------------------- Retries
        [Fact]
        public async Task ReadRetries_RetryTheFailedPage_WithSmallerPages()
        {
            var client = Odoo();
            var failures = 0;
            client.FailOnCall = call => call.StartsWith("search_read", StringComparison.Ordinal) && failures++ < 2 ? new OdooUnavailableException("502 Bad Gateway") : null;
            var logger = new ListLogger();

            var output = await Provider(client, p =>
            {
                p.PageSize = 40;
                p.ReadRetries = 3;
                p.ReadRetryDelaySeconds = 0;
            }).ActAsync([], TestContexts.Create(logger: logger));

            // 40, then 20, then 10 (the minimum): the successful page is read with the reduced size.
            Assert.Equal(3, output.Count);
            Assert.Equal(2, logger.Warnings.Count(w => w.Contains("nouvelle tentative")));
            Assert.Equal(10, client.Pages[0].Limit);
            Assert.Contains(logger.Warnings, w => w.Contains("pages de 10"));
        }

        [Fact]
        public async Task WithoutReadRetries_TheFirstFailureMakesTheSourceUnavailable()
        {
            var client = Odoo();
            var failures = 0;
            client.FailOnCall = call => call.StartsWith("search_read", StringComparison.Ordinal) && failures++ < 1 ? new OdooUnavailableException("502 Bad Gateway") : null;

            await Assert.ThrowsAsync<SourceUnavailableException>(() => Provider(client).ActAsync([], TestContexts.Create()));
        }

        [Fact]
        public async Task ReadRetries_GiveUpAfterTheLastAttempt()
        {
            var client = Odoo();
            client.FailOnCall = call => call.StartsWith("search_read", StringComparison.Ordinal) ? new OdooUnavailableException("502 Bad Gateway") : null;

            await Assert.ThrowsAsync<SourceUnavailableException>(() => Provider(client, p =>
            {
                p.ReadRetries = 2;
                p.ReadRetryDelaySeconds = 0;
            }).ActAsync([], TestContexts.Create()));

            Assert.Empty(client.Pages);
        }
    }
}
