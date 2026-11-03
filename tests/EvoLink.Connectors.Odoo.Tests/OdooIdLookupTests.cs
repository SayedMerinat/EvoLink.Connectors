using EvoLink.Connectors.Odoo.Client;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Odoo.Tests
{
    public class OdooIdLookupTests
    {
        private static TestOdooIdLookup Lookup(FakeOdooClient client, Action<OdooIdLookupTransformer>? configure = null)
        {
            var lookup = new TestOdooIdLookup(client)
            {
                Url = "https://odoo.test",
                Database = "odoo",
                Username = "ortolan",
                ApiKey = "key",
                Model = "res.partner",
                SourceProperty = "customerRef",
                OdooField = "ref",
                TargetProperty = "partner_id",
            };

            configure?.Invoke(lookup);
            return lookup;
        }

        private static FakeOdooClient Odoo()
        {
            var client = FakeOdooClient.Partners();
            client.Seed("res.partner", new() { ["name"] = "EvoLink SA", ["ref"] = "P7" }, id: 7);
            client.Seed("res.partner", new() { ["name"] = "Ancien", ["ref"] = "P8", ["active"] = false }, id: 8);
            return client;
        }

        [Fact]
        public async Task FoundRecord_WritesItsIdAndTheSearchesAreCached()
        {
            var client = Odoo();
            var input = TestContexts.Tracked(
                new Entity { ["customerRef"] = "P7" },
                new Entity { ["customerRef"] = "P7" },
                new Entity { ["customerRef"] = "P7" });

            var output = await Lookup(client, l => l.Filters = [new OdooDomainFilter { Field = "is_company", Operator = "=", Value = false }]).ActAsync(input, TestContexts.Create());

            Assert.All(output, e => Assert.Equal(7L, e["partner_id"]));
            Assert.Single(client.Calls, c => c == "search res.partner");
            Assert.Equal("""[["is_company","=",false],["ref","=","P7"]]""", Assert.Single(client.Domains).ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(1, client.Disposed);
        }

        [Fact]
        public async Task ParallelEntities_ShareTheCache()
        {
            var client = Odoo();
            var input = TestContexts.Tracked(Enumerable.Range(0, 40).Select(i => new Entity { ["customerRef"] = i % 2 == 0 ? "P7" : "INCONNU" }).ToArray());

            var output = await Lookup(client, l => l.MaxDegreeOfParallelism = 8).ActAsync(input, TestContexts.Create());

            Assert.Equal(20, output.Count(e => Equals(e["partner_id"], 7L)));
            Assert.Equal(20, output.Count(e => e["partner_id"] is null));
            Assert.Equal(2, client.Calls.Count(c => c == "search res.partner"));
        }

        [Fact]
        public async Task NotFound_IsAWarningAndNull()
        {
            var client = Odoo();
            var logger = new ListLogger();
            var input = TestContexts.Tracked(new Entity { ["customerRef"] = "P8" }, new Entity { ["customerRef"] = "" }, new Entity { ["other"] = 1 });

            var output = await Lookup(client).ActAsync(input, TestContexts.Create(logger: logger));

            Assert.All(output, e => Assert.Null(e["partner_id"]));
            Assert.All(output, e => Assert.NotEqual(SyncResult.Failed, e.SyncResult));
            Assert.Equal(3, logger.Warnings.Count());
        }

        [Fact]
        public async Task IncludeArchived_FindsTheArchivedRecords()
        {
            var client = Odoo();

            var output = await Lookup(client, l => l.IncludeArchived = true).ActAsync(TestContexts.Tracked(new Entity { ["customerRef"] = "P8" }), TestContexts.Create());

            Assert.Equal(8L, Assert.Single(output)["partner_id"]);
        }

        [Fact]
        public async Task NotFound_FailsTheEntityWithFailWhenNotFound()
        {
            var client = Odoo();
            var input = TestContexts.Tracked(new Entity { ["customerRef"] = "INCONNU" }, new Entity { ["customerRef"] = "P7" });

            var output = await Lookup(client, l => l.FailWhenNotFound = true).ActAsync(input, TestContexts.Create());

            Assert.Equal(SyncResult.Failed, output[0].SyncResult);
            Assert.Contains("INCONNU", output[0].Error);
            Assert.Equal(7L, output[1]["partner_id"]);
        }

        [Fact]
        public async Task UnavailableOdoo_IsASourceUnavailability()
        {
            var client = Odoo();
            var lookup = Lookup(client);
            client.FailWith = new OdooUnavailableException("Odoo https://odoo.test n'a pas répondu dans le délai de 100 s.");

            await Assert.ThrowsAsync<SourceUnavailableException>(() => lookup.ActAsync(TestContexts.Tracked(new Entity { ["customerRef"] = "P7" }), TestContexts.Create()));
        }

        [Fact]
        public async Task UnavailableOdoo_DuringTheSearch_IsASourceUnavailabilityAndIsNotCached()
        {
            var client = Odoo();
            var fail = true;
            client.FailOnCall = call => fail && call.StartsWith("search ", StringComparison.Ordinal) ? new OdooUnavailableException("Odoo est injoignable.") : null;
            var lookup = Lookup(client);

            await Assert.ThrowsAsync<SourceUnavailableException>(() => lookup.ActAsync(TestContexts.Tracked(new Entity { ["customerRef"] = "P7" }), TestContexts.Create()));

            fail = false;
            var output = await lookup.ActAsync(TestContexts.Tracked(new Entity { ["customerRef"] = "P7" }), TestContexts.Create());
            Assert.Equal(7L, Assert.Single(output)["partner_id"]);
        }

        [Fact]
        public async Task InvalidConfiguration_IsReported()
        {
            var client = Odoo();
            var input = TestContexts.Tracked(new Entity { ["customerRef"] = "P7" });

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Lookup(client, l => l.TargetProperty = "").ActAsync(input, TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Lookup(client, l => l.Username = "").ActAsync(input, TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Lookup(client, l => l.OdooField = "inconnu").ActAsync(input, TestContexts.Create()));
        }
    }
}
