using EvoLink.Connectors.Odoo.Client;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Odoo.Tests
{
    public class OdooSynchroniserTests
    {
        private static TestOdooSynchroniser Synchroniser(FakeOdooClient client, Action<OdooSynchroniser>? configure = null)
        {
            var synchroniser = new TestOdooSynchroniser(client)
            {
                Url = "https://odoo.test",
                Database = "odoo",
                Username = "ortolan",
                ApiKey = "key",
                Model = "res.partner",
                TimeZone = "Europe/Zurich",
                Filters = [new OdooDomainFilter { Field = "is_company", Operator = "=", Value = false }],
            };

            configure?.Invoke(synchroniser);
            return synchroniser;
        }

        private static List<Entity> Source(string aliceName = "Alice")
            => TestContexts.Tracked(
                new Entity
                {
                    ["ref"] = "C1",
                    ["name"] = aliceName,
                    ["email"] = "alice@example.ch",
                    ["parent_id"] = 7L,
                    ["credit_limit"] = "1'500.50",
                    ["employees"] = "12",
                    ["birthday"] = new DateTime(1990, 5, 17),
                    ["last_contact"] = "15.01.2024 10:30",
                    ["category_id"] = "1;2",
                    ["notInOdoo"] = "x",
                },
                new Entity
                {
                    ["ref"] = "C2",
                    ["name"] = "Bob",
                    ["email"] = null,
                    ["parent_id"] = null,
                    ["credit_limit"] = null,
                    ["employees"] = null,
                    ["birthday"] = null,
                    ["last_contact"] = null,
                    ["category_id"] = null,
                    ["notInOdoo"] = "y",
                });

        private static FakeOdooClient Odoo(out long companyId, out long obsoleteId)
        {
            var client = FakeOdooClient.Partners();
            companyId = client.Seed("res.partner", new() { ["name"] = "EvoLink SA", ["ref"] = "P7", ["is_company"] = true }, id: 7);
            obsoleteId = client.Seed("res.partner", new() { ["name"] = "Obsolète", ["ref"] = "C3" });
            return client;
        }

        [Fact]
        public async Task Mirror_CreatesUpdatesAndDeletesWithConvertedValues()
        {
            var client = Odoo(out var companyId, out var obsoleteId);
            var source = Source();

            var output = await Synchroniser(client).ActAsync(source, TestContexts.Create(SyncMode.Mirror));

            Assert.Equal(2, output.Count(e => e.SyncResult == SyncResult.Created));
            Assert.Single(output, e => e.SyncResult == SyncResult.Deleted);
            Assert.False(client.Data["res.partner"].ContainsKey(obsoleteId));
            Assert.True(client.Data["res.partner"].ContainsKey(companyId), "The records outside the filters are not managed.");

            var aliceId = long.Parse(source[0].Metadata[OdooSynchroniser.IdMetadataKey], System.Globalization.CultureInfo.InvariantCulture);
            var alice = client.Record("res.partner", aliceId);

            Assert.Equal("Alice", alice["name"]);
            Assert.Equal(1500.5m, alice["credit_limit"]);
            Assert.Equal(12L, alice["employees"]);
            Assert.Equal("1990-05-17", alice["birthday"]);
            Assert.Equal("2024-01-15 09:30:00", alice["last_contact"]);
            Assert.Equal(7L, alice["parent_id"]);
            Assert.Equal([1L, 2L], (List<long>)alice["category_id"]!);
            Assert.False(alice.ContainsKey("notInOdoo"));
            Assert.Equal("res.partner", source[0].Metadata[OdooSynchroniser.ModelMetadataKey]);

            // The entities leaving the module keep their original values.
            Assert.Same(source[0], output.First(e => e.GetString("ref") == "C1"));
            Assert.Equal("1'500.50", source[0]["credit_limit"]);
            Assert.Equal(new DateTime(1990, 5, 17), source[0]["birthday"]);

            // Second run: Odoo returns false for the empty fields, [id, "name"] for a many2one, floats...: nothing changes.
            client.Calls.Clear();
            var second = await Synchroniser(client).ActAsync(Source(), TestContexts.Create(SyncMode.Mirror));

            Assert.All(second, e => Assert.Equal(SyncResult.Unchanged, e.SyncResult));
            Assert.DoesNotContain(client.Calls, c => c.StartsWith("write", StringComparison.Ordinal) || c.StartsWith("create", StringComparison.Ordinal) || c.StartsWith("unlink", StringComparison.Ordinal));

            // Third run: only the modified field is written.
            client.Calls.Clear();
            var third = await Synchroniser(client).ActAsync(Source(aliceName: "Alicia"), TestContexts.Create());

            Assert.Equal(SyncResult.Updated, third.Single(e => e.GetString("ref") == "C1").SyncResult);
            Assert.Equal(SyncResult.Unchanged, third.Single(e => e.GetString("ref") == "C2").SyncResult);
            Assert.Equal([$"write res.partner {aliceId} name"], client.Calls.Where(c => c.StartsWith("write", StringComparison.Ordinal)));
            Assert.Equal("Alicia", alice["name"]);
        }

        [Fact]
        public async Task TargetRead_UsesTheFiltersTheContextAndOnlyTheNeededFields()
        {
            var client = Odoo(out _, out _);

            await Synchroniser(client, s => { s.Language = "fr_CH"; s.Domain = """["|", ["email", "=", false], ["email", "!=", false]]"""; }).ActAsync(Source(), TestContexts.Create());

            var domain = Assert.Single(client.Domains);
            Assert.Equal("""[["is_company","=",false],"|",["email","=",false],["email","!=",false]]""", domain.ToString(Newtonsoft.Json.Formatting.None));
            Assert.All(client.Contexts, c => Assert.Equal("fr_CH", c!.Language));
        }

        [Fact]
        public async Task Archive_WritesActiveFalseInsteadOfDeleting()
        {
            var client = Odoo(out _, out var obsoleteId);

            var output = await Synchroniser(client, s => s.ArchiveInsteadOfDelete = true).ActAsync([], TestContexts.Create(SyncMode.Mirror));

            Assert.Equal(SyncResult.Deleted, Assert.Single(output).SyncResult);
            Assert.Equal(false, client.Record("res.partner", obsoleteId)["active"]);
            Assert.Contains($"write res.partner {obsoleteId} active", client.Calls);
            Assert.DoesNotContain(client.Calls, c => c.StartsWith("unlink", StringComparison.Ordinal));
        }

        [Fact]
        public async Task DeleteAction_InIncrementalMode_UnlinksTheRecordFoundByItsKey()
        {
            var client = Odoo(out _, out var obsoleteId);
            var deletion = new Entity { ["ref"] = "C3", SyncAction = SyncAction.Delete };

            var output = await Synchroniser(client).ActAsync(TestContexts.Tracked(deletion), TestContexts.Create());

            Assert.Equal(SyncResult.Deleted, Assert.Single(output).SyncResult);
            Assert.Contains($"unlink res.partner {obsoleteId}", client.Calls);
        }

        [Fact]
        public async Task Relations_AreResolvedByNameWithACache()
        {
            var client = Odoo(out _, out _);
            var source = TestContexts.Tracked(
                new Entity { ["ref"] = "C1", ["name"] = "Alice", ["parent_id"] = "EvoLink SA", ["category_id"] = "VIP;Fournisseur" },
                new Entity { ["ref"] = "C2", ["name"] = "Bob", ["parent_id"] = "EvoLink SA", ["category_id"] = "[1, Fournisseur]" });

            var output = await Synchroniser(client, s => s.ResolveRelationsByName = true).ActAsync(source, TestContexts.Create());

            Assert.All(output, e => Assert.Equal(SyncResult.Created, e.SyncResult));
            Assert.All(client.Data["res.partner"].Values.Where(r => (string?)r.GetValueOrDefault("ref") is "C1" or "C2"), r =>
            {
                Assert.Equal(7L, r["parent_id"]);
                Assert.Equal([1L, 2L], (List<long>)r["category_id"]!);
            });

            Assert.Single(client.Calls, c => c == "name_search res.partner = EvoLink SA");
            Assert.Single(client.Calls, c => c == "name_search res.partner.category = Fournisseur");
        }

        [Fact]
        public async Task UnknownRelationName_IsAWarningAndTheFieldIsNotWritten()
        {
            var client = Odoo(out _, out _);
            var logger = new ListLogger();
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C1", ["name"] = "Alice", ["parent_id"] = "Inconnue SA" });

            var output = await Synchroniser(client, s => s.ResolveRelationsByName = true).ActAsync(source, TestContexts.Create(logger: logger));

            Assert.Equal(SyncResult.Created, Assert.Single(output).SyncResult);
            Assert.Contains(logger.Warnings, w => w.Contains("Inconnue SA", StringComparison.Ordinal));
            Assert.Contains("create res.partner name,ref", client.Calls);
        }

        [Fact]
        public async Task UnknownRelationName_FailsTheEntityWithFailWhenNotFound()
        {
            var client = Odoo(out _, out _);
            var source = TestContexts.Tracked(
                new Entity { ["ref"] = "C1", ["name"] = "Alice", ["parent_id"] = "Inconnue SA" },
                new Entity { ["ref"] = "C2", ["name"] = "Bob", ["parent_id"] = 7L });

            var output = await Synchroniser(client, s => { s.ResolveRelationsByName = true; s.FailWhenNotFound = true; }).ActAsync(source, TestContexts.Create());

            var alice = output.Single(e => e.GetString("ref") == "C1");
            Assert.Equal(SyncResult.Failed, alice.SyncResult);
            Assert.Contains("Inconnue SA", alice.Error);
            Assert.Equal(SyncResult.Created, output.Single(e => e.GetString("ref") == "C2").SyncResult);
        }

        [Fact]
        public async Task RelationName_WithoutTheOption_FailsTheEntity()
        {
            var client = Odoo(out _, out _);
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C1", ["name"] = "Alice", ["parent_id"] = "EvoLink SA" });

            var output = await Synchroniser(client).ActAsync(source, TestContexts.Create());

            Assert.Equal(SyncResult.Failed, Assert.Single(output).SyncResult);
            Assert.Contains("resolveRelationsByName", output[0].Error);
            Assert.DoesNotContain(client.Calls, c => c.StartsWith("name_search", StringComparison.Ordinal));
        }

        [Fact]
        public async Task InvalidValue_FailsOnlyItsEntity()
        {
            var client = Odoo(out _, out _);
            var source = TestContexts.Tracked(
                new Entity { ["ref"] = "C1", ["name"] = "Alice", ["credit_limit"] = "beaucoup" },
                new Entity { ["ref"] = "C2", ["name"] = "Bob", ["credit_limit"] = "12,5" });

            var output = await Synchroniser(client).ActAsync(source, TestContexts.Create());

            var alice = output.Single(e => e.GetString("ref") == "C1");
            Assert.Equal(SyncResult.Failed, alice.SyncResult);
            Assert.Contains("credit_limit", alice.Error);
            Assert.Equal(SyncResult.Created, output.Single(e => e.GetString("ref") == "C2").SyncResult);
            Assert.Contains(client.Data["res.partner"].Values, r => (string?)r.GetValueOrDefault("ref") == "C2" && Equals(r["credit_limit"], 12.5m));
        }

        [Fact]
        public async Task OdooBusinessError_FailsTheEntityWithTheOdooMessage()
        {
            var client = Odoo(out _, out _);
            client.CreateError = values => (string?)values["ref"] == "C1"
                ? new OdooRpcException("Odoo a refusé l'opération 'create' sur le modèle 'res.partner' : Adresse e-mail invalide.", "Adresse e-mail invalide.", "odoo.exceptions.ValidationError")
                : null;

            var output = await Synchroniser(client).ActAsync(Source(), TestContexts.Create());

            var alice = output.Single(e => e.GetString("ref") == "C1");
            Assert.Equal(SyncResult.Failed, alice.SyncResult);
            Assert.Contains("Adresse e-mail invalide.", alice.Error);
            Assert.Equal(SyncResult.Created, output.Single(e => e.GetString("ref") == "C2").SyncResult);
        }

        [Fact]
        public async Task UnavailableOdoo_DuringTheWrite_IsATargetUnavailability()
        {
            var client = Odoo(out _, out _);
            client.CreateError = _ => new OdooUnavailableException("Odoo https://odoo.test est injoignable : connexion refusée");

            var ex = await Assert.ThrowsAsync<TargetUnavailableException>(() => Synchroniser(client).ActAsync(Source(), TestContexts.Create()));

            Assert.Contains("injoignable", ex.Message);
            Assert.Equal(1, client.Disposed);
        }

        [Fact]
        public async Task UnavailableOdoo_AtTheStart_IsATargetUnavailability()
        {
            var client = Odoo(out _, out _);
            client.FailWith = new OdooUnavailableException("Authentification refusée par Odoo");

            await Assert.ThrowsAsync<TargetUnavailableException>(() => Synchroniser(client).ActAsync(Source(), TestContexts.Create()));
        }

        [Fact]
        public async Task FieldMapping_RenamesThePropertiesAndOnlyMappedFieldsRestrictsTheWrite()
        {
            var client = Odoo(out _, out _);
            var source = TestContexts.Tracked(new Entity { ["Reference"] = "C1", ["Nom"] = "Alice", ["email"] = "alice@example.ch" });

            var output = await Synchroniser(client, s =>
            {
                s.Fields = new() { ["Reference"] = "ref", ["Nom"] = "name" };
                s.OnlyMappedFields = true;
                s.KeyFields = ["Reference"];
            }).ActAsync(source, TestContexts.Create());

            Assert.Equal(SyncResult.Created, Assert.Single(output).SyncResult);
            Assert.Contains("create res.partner name,ref", client.Calls);

            client.Calls.Clear();
            var second = await Synchroniser(client, s =>
            {
                s.Fields = new() { ["Reference"] = "ref", ["Nom"] = "name" };
                s.OnlyMappedFields = true;
                s.KeyFields = ["Reference"];
            }).ActAsync(TestContexts.Tracked(new Entity { ["Reference"] = "C1", ["Nom"] = "Alice", ["email"] = "autre@example.ch" }), TestContexts.Create());

            Assert.Equal(SyncResult.Unchanged, Assert.Single(second).SyncResult);
        }

        [Fact]
        public async Task ReadonlyMagicAndOne2ManyFields_AreNeverWritten()
        {
            var client = Odoo(out _, out _);
            var source = TestContexts.Tracked(new Entity { ["ref"] = "C1", ["name"] = "Alice", ["display_name"] = "x", ["write_date"] = "2024-01-01", ["child_ids"] = "1", ["total_due"] = 5, ["id"] = 3 });

            var output = await Synchroniser(client).ActAsync(source, TestContexts.Create());

            Assert.Equal(SyncResult.Created, Assert.Single(output).SyncResult);
            Assert.Contains("create res.partner name,ref", client.Calls);
        }

        [Fact]
        public async Task InvalidConfiguration_IsReported()
        {
            var client = Odoo(out _, out _);

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => s.Url = "").ActAsync(Source(), TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => s.Url = "odoo.test").ActAsync(Source(), TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => s.ApiKey = " ").ActAsync(Source(), TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => s.Domain = "{ pas du json").ActAsync(Source(), TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => s.TimeZone = "Mars/Olympus").ActAsync(Source(), TestContexts.Create()));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client).ActAsync(Source(), TestContexts.Create(keyFields: ["inconnu"])));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client).ActAsync(Source(), TestContexts.Create(keyFields: [])));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => s.Model = "res.inconnu").ActAsync(Source(), TestContexts.Create()));

            client.Define("res.country", new OdooFieldInfo("id", "integer"), new OdooFieldInfo("code", "char"), new OdooFieldInfo("name", "char"));
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Synchroniser(client, s => { s.Model = "res.country"; s.ArchiveInsteadOfDelete = true; s.Filters = []; })
                .ActAsync(TestContexts.Tracked(new Entity { ["code"] = "CH", ["name"] = "Suisse" }), TestContexts.Create(keyFields: ["code"])));

            Assert.DoesNotContain(client.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        }
    }
}
