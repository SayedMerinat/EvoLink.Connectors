using EvoLink.Odoo.Client;
using Newtonsoft.Json;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Odoo.Tests
{
    public class OdooProviderTests
    {
        private static TestOdooProvider Provider(FakeOdooClient client, Action<OdooProvider>? configure = null)
        {
            var provider = new TestOdooProvider(client)
            {
                Url = "https://odoo.test",
                Database = "odoo",
                Username = "ortolan",
                ApiKey = "key",
                Model = "res.partner",
                TimeZone = "Europe/Zurich",
            };

            configure?.Invoke(provider);
            return provider;
        }

        private static FakeOdooClient Odoo()
        {
            var client = FakeOdooClient.Partners();
            client.Seed("res.partner", new() { ["name"] = "EvoLink SA", ["ref"] = "P7", ["is_company"] = true, ["write_date"] = "2024-01-01 08:00:00" }, id: 7);
            client.Seed("res.partner", new()
            {
                ["name"] = "Alice",
                ["ref"] = "C1",
                ["parent_id"] = 7L,
                ["birthday"] = "1990-05-17",
                ["last_contact"] = "2024-07-01 08:30:00",
                ["credit_limit"] = 1500.5m,
                ["category_id"] = new List<long> { 2, 1 },
                ["write_date"] = "2024-06-01 12:00:00",
            }, id: 10);
            client.Seed("res.partner", new() { ["name"] = "Archivé", ["ref"] = "C9", ["active"] = false, ["write_date"] = "2024-06-01 12:00:00" }, id: 11);
            return client;
        }

        [Fact]
        public async Task Fetch_NormalisesTheOdooValues()
        {
            var client = Odoo();

            var output = await Provider(client, p => p.IncludeMany2OneNames = true).ActAsync([], TestContexts.Create());

            Assert.Equal(2, output.Count);
            var alice = output.Single(e => e.GetString("ref") == "C1");

            Assert.Equal(10L, alice["id"]);
            Assert.Equal(7L, alice["parent_id"]);
            Assert.Equal("EvoLink SA", alice["parent_id_name"]);
            Assert.Null(alice["email"]);
            Assert.Equal(false, alice["is_company"]);
            Assert.Equal(1500.5m, alice["credit_limit"]);
            Assert.Equal(new DateTime(1990, 5, 17), alice["birthday"]);
            Assert.Equal(new DateTime(2024, 7, 1, 10, 30, 0), alice["last_contact"]);
            Assert.Equal(new List<object?> { 1L, 2L }, alice["category_id"]);
            Assert.Equal("10", alice.Metadata[OdooSynchroniser.IdMetadataKey]);
            Assert.Equal("res.partner", alice.Metadata[OdooSynchroniser.ModelMetadataKey]);
            Assert.Equal(1, client.Disposed);
        }

        [Fact]
        public async Task Fetch_AppliesTheFiltersTheFieldsTheMappingAndTheContext()
        {
            var client = Odoo();

            var output = await Provider(client, p =>
            {
                p.Filters = [new OdooDomainFilter { Field = "is_company", Operator = "=", Value = false }];
                p.Domain = """[["ref", "in", ["C1", "C9"]]]""";
                p.Fields = new() { ["Reference"] = "ref", ["Nom"] = "name" };
                p.OnlyMappedFields = true;
                p.Language = "fr_CH";
                p.IncludeArchived = true;
            }).ActAsync([], TestContexts.Create());

            Assert.Equal("""[["is_company","=",false],["ref","in",["C1","C9"]]]""", Assert.Single(client.Domains).ToString(Formatting.None));
            Assert.All(client.Contexts, c => { Assert.Equal("fr_CH", c!.Language); Assert.False(c.ActiveTest); });

            Assert.Equal(2, output.Count);
            Assert.All(output, e => Assert.Equal(["Nom", "Reference"], e.Properties.Keys.Order()));
            Assert.Contains(output, e => e.GetString("Reference") == "C9");
        }

        [Fact]
        public async Task Fetch_ReadsEveryPage()
        {
            var client = FakeOdooClient.Partners();
            for (var i = 1; i <= 5; i++)
                client.Seed("res.partner", new() { ["name"] = $"P{i}", ["ref"] = $"R{i}" });

            var output = await Provider(client, p => { p.PageSize = 2; p.ReadFields = ["ref"]; }).ActAsync([], TestContexts.Create());

            Assert.Equal(5, output.Count);
            Assert.Equal([(0, 2), (2, 2), (4, 2)], client.Pages);
            Assert.All(output, e => Assert.Equal(["id", "ref"], e.Properties.Keys.Order()));
        }

        [Fact]
        public async Task Incremental_ReadsTheRecordsModifiedSinceTheLastSuccessfulRun()
        {
            var client = Odoo();
            var logger = new ListLogger();
            var lastRun = new DateTimeOffset(2024, 5, 31, 14, 10, 0, TimeSpan.FromHours(2));

            var output = await Provider(client, p => p.Incremental = true).ActAsync([], TestContexts.Create(lastRun: lastRun, logger: logger));

            Assert.Equal("""[["write_date",">","2024-05-31 12:00:00"]]""", Assert.Single(client.Domains).ToString(Formatting.None));
            Assert.Equal("C1", Assert.Single(output).GetString("ref"));
        }

        [Theory]
        [InlineData(SyncMode.Mirror, true)]
        [InlineData(SyncMode.Full, true)]
        [InlineData(SyncMode.Incremental, false)]
        public async Task Incremental_ReadsEverythingInMirrorFullOrWithoutPreviousRun(SyncMode mode, bool withLastRun)
        {
            var client = Odoo();

            var output = await Provider(client, p => p.Incremental = true).ActAsync([], TestContexts.Create(mode, lastRun: withLastRun ? DateTimeOffset.UtcNow : null));

            Assert.Equal("[]", Assert.Single(client.Domains).ToString(Formatting.None));
            Assert.Equal(2, output.Count);
        }

        [Fact]
        public async Task UnavailableOdoo_IsASourceUnavailability()
        {
            var client = Odoo();
            client.FailWith = new OdooUnavailableException("Odoo https://odoo.test a répondu 503 Service Unavailable.");

            var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => Provider(client).ActAsync([], TestContexts.Create()));

            Assert.Contains("503", ex.Message);
            Assert.DoesNotContain("key", ex.Message);
        }

        [Fact]
        public async Task OdooBusinessError_WhileReading_FailsTheJob()
        {
            var client = Odoo();
            client.FailWith = new OdooRpcException("Odoo a refusé l'opération : champ invalide", "Invalid field");

            await Assert.ThrowsAsync<OdooRpcException>(() => Provider(client).ActAsync([], TestContexts.Create()));
        }

        [Fact]
        public async Task InvalidConfiguration_IsReported()
        {
            var client = Odoo();

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => p.Model = "").ActAsync([], TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => p.Database = "").ActAsync([], TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => p.ReadFields = ["inconnu"]).ActAsync([], TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => p.Domain = """[["a", "="]]""").ActAsync([], TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => p.Filters = [new OdooDomainFilter { Field = "" }]).ActAsync([], TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => { p.Incremental = true; p.IncrementalField = "name"; }).ActAsync([], TestContexts.Create(lastRun: DateTimeOffset.UtcNow)));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Provider(client, p => p.TimeoutSeconds = 0).ActAsync([], TestContexts.Create()));
        }
    }
}
