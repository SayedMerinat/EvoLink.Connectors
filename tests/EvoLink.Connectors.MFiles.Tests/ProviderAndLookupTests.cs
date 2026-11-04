using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.MFiles.Tests
{
    public class ProviderTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();

        public ProviderTests()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.NameOrTitle, "Facture 1"), (V.Country, new MFilesLookup(1, "Suisse")),
                (V.Categories, new List<MFilesLookup> { new(1, "Contrat"), new(3, "Offre") }));
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-2"), (V.NameOrTitle, "Facture 2"), (V.Country, new MFilesLookup(2, "France")));
            _vault.AddObject(V.DocumentClass, (V.Number, "D-1"));
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-3")).Deleted = true;
        }

        [Fact]
        public async Task Fetch_ReadsTheObjectsOfTheClass_WithTheirProperties()
        {
            var provider = new TestProvider(_vault) { Vault = "Test", Class = "CL.Facture" };

            var entities = await provider.FetchAsync(V.Context());

            Assert.Equal(["F-1", "F-2"], entities.Select(e => e.GetString("Numéro")).Order());
            var first = entities.Single(e => e.GetString("Numéro") == "F-1");
            Assert.Equal("Suisse", first["Pays"]);
            Assert.Equal(new object?[] { "Contrat", "Offre" }, Assert.IsAssignableFrom<IEnumerable<object?>>(first["Catégories"]));
            Assert.Equal(_vault.Objects[0].Id.ToString(), first.Metadata[MFilesKeys.MetadataObjectId]);
            Assert.Equal(1, _vault.Calls("Disconnect"));
        }

        [Fact]
        public async Task Fetch_AppliesTheConditionsTheMappingAndTheIdOptions()
        {
            var provider = new TestProvider(_vault)
            {
                Vault = "Test",
                Class = "5",
                Conditions = new() { ["PD.Pays"] = "FR" },
                Fields = new() { ["numero"] = "Numéro", ["pays"] = "1006" },
                OnlyMappedFields = true,
                LookupsAsIds = true,
                ObjectIdProperty = "id",
            };

            var entity = Assert.Single(await provider.FetchAsync(V.Context()));

            Assert.Equal(["id", "numero", "pays"], entity.Properties.Keys.Order());
            Assert.Equal("F-2", entity["numero"]);
            Assert.Equal(2L, entity["pays"]);
            Assert.Equal((long)_vault.Objects[1].Id, entity["id"]);
        }

        [Fact]
        public async Task Fetch_ReadsOnlyTheRequestedProperties()
        {
            var provider = new TestProvider(_vault) { Vault = "Test", Class = "Facture", Properties = ["Numéro", "Montant"] };

            var entities = await provider.FetchAsync(V.Context());

            Assert.All(entities, e => Assert.Equal(["Montant", "Numéro"], e.Properties.Keys.Order()));
            Assert.All(entities, e => Assert.Null(e["Montant"]));
        }

        [Fact]
        public async Task Fetch_UnknownConditionValue_IsAConfigurationError()
        {
            var provider = new TestProvider(_vault) { Vault = "Test", Class = "Facture", Conditions = new() { ["Pays"] = "Atlantide" } };

            var error = await Assert.ThrowsAsync<OrtolanConfigurationException>(() => provider.FetchAsync(V.Context()));
            Assert.Contains("Atlantide", error.Message);
            Assert.Equal(1, _vault.Calls("Dispose"));
        }
    }

    public class UserLookupTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();
        private readonly FakeObject _contact;

        public UserLookupTests()
        {
            _contact = _vault.AddObject(V.ContactClass, (V.ExternalLogin, "j.doe@client.ch"), (V.Account, new MFilesLookup(V.JDoe)));
            _vault.AddObject(V.ContactClass, (V.ExternalLogin, "double@client.ch"), (V.Account, new MFilesLookup(V.Admin)));
            _vault.AddObject(V.ContactClass, (V.ExternalLogin, "double@client.ch"), (V.Account, new MFilesLookup(V.ASmith)));
            _vault.AddObject(V.ContactClass, (V.ExternalLogin, "sans.compte@client.ch"));
        }

        private TestUserLookup Lookup(Action<MFilesUserLookup>? configure = null)
        {
            var lookup = new TestUserLookup(_vault) { Vault = "Test", Class = "Contact externe", SourceProperty = "Auteur", MatchField = "Login externe", UserField = "Compte" };
            configure?.Invoke(lookup);
            return lookup;
        }

        [Fact]
        public async Task Found_WritesTheObjectIdAndTheLogin()
        {
            var entity = V.Entity(("Auteur", "j.doe@client.ch"));

            var output = await Lookup().ActAsync([entity], V.Context());

            Assert.Equal((long)_contact.Id, output[0]["Auteur"]);
            Assert.Equal(@"DOMAIN\jdoe", output[0][MFilesKeys.CreatedBy]);
            Assert.Equal(@"DOMAIN\jdoe", output[0][MFilesKeys.LastModifiedBy]);
        }

        [Fact]
        public async Task Found_WithConfiguredTargets()
        {
            var lookup = Lookup(l => { l.IdProperty = "ContactId"; l.LoginProperties = ["login"]; });

            var output = await lookup.ActAsync([V.Entity(("Auteur", "j.doe@client.ch"))], V.Context());

            Assert.Equal("j.doe@client.ch", output[0]["Auteur"]);
            Assert.Equal((long)_contact.Id, output[0]["ContactId"]);
            Assert.Equal(@"DOMAIN\jdoe", output[0]["login"]);
            Assert.False(output[0].Has(MFilesKeys.CreatedBy));
        }

        [Theory]
        [InlineData("inconnu@client.ch")]
        [InlineData("double@client.ch")]
        [InlineData("sans.compte@client.ch")]
        public async Task NotFound_WarnsAndLeavesTheEntity_OrFailsIt(string value)
        {
            var logger = new CapturingLogger();
            var output = await Lookup().ActAsync([V.Entity(("Auteur", value))], V.Context(logger: logger));

            Assert.NotEqual(SyncResult.Failed, output[0].SyncResult);
            Assert.False(output[0].Has(MFilesKeys.CreatedBy));
            Assert.Single(logger.Warnings);

            var failed = await Lookup(l => l.FailWhenNotFound = true).ActAsync([V.Entity(("Auteur", value))], V.Context());
            Assert.Equal(SyncResult.Failed, failed[0].SyncResult);
        }

        [Fact]
        public async Task NotFound_WithClearWhenNotFound_EmptiesTheDestination()
        {
            var logger = new CapturingLogger();
            var output = await Lookup(l => l.ClearWhenNotFound = true).ActAsync([V.Entity(("Auteur", "inconnu@client.ch"))], V.Context(logger: logger));

            Assert.NotEqual(SyncResult.Failed, output[0].SyncResult);
            Assert.True(output[0].Has("Auteur"));
            Assert.Null(output[0]["Auteur"]);
            Assert.Contains(logger.Warnings, w => w.Contains("vidée"));
        }

        [Fact]
        public async Task SameValue_IsSearchedOnce()
        {
            var entities = Enumerable.Range(0, 20).Select(_ => V.Entity(("Auteur", "j.doe@client.ch"))).ToList();

            await Lookup(l => l.MaxDegreeOfParallelism = 4).ActAsync(entities, V.Context());

            Assert.InRange(_vault.Calls("Search"), 1, 4);
            Assert.Equal(1, _vault.Calls("GetUserAccounts"));
        }

        [Fact]
        public async Task EmptyValue_LeavesTheEntityUntouched()
        {
            var output = await Lookup().ActAsync([V.Entity(("Auteur", " "))], V.Context());

            Assert.Equal(" ", output[0]["Auteur"]);
            Assert.Equal(0, _vault.Calls("Search"));
        }

        [Fact]
        public async Task UserField_NotPointingToUsers_IsAConfigurationError()
        {
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Lookup(l => l.UserField = "Pays").ActAsync([V.Entity(("Auteur", "x"))], V.Context()));
        }
    }

    public class ValueLookupTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();

        private TestValueLookup Lookup(Action<MFilesValueLookup>? configure = null)
        {
            var lookup = new TestValueLookup(_vault) { Vault = "Test", ValueList = "VL.Pays", SourceProperty = "Pays" };
            configure?.Invoke(lookup);
            return lookup;
        }

        [Fact]
        public async Task Name_IsReplacedByTheId()
        {
            var output = await Lookup().ActAsync([V.Entity(("Pays", "france")), V.Entity(("Pays", "CH"))], V.Context());

            Assert.Equal(2L, output[0]["Pays"]);
            Assert.Equal(1L, output[1]["Pays"]);
        }

        [Fact]
        public async Task SeveralNames_GiveSeveralIds_FromTheValueListOfAProperty()
        {
            var lookup = Lookup(l => { l.ValueList = null; l.Property = "Catégories"; l.SourceProperty = "cat"; l.TargetProperty = "catIds"; l.Separator = ","; });

            var output = await lookup.ActAsync([V.Entity(("cat", "Contrat, Offre"))], V.Context());

            Assert.Equal(new object?[] { 1L, 3L }, Assert.IsAssignableFrom<IEnumerable<object?>>(output[0]["catIds"]));
            Assert.Equal("Contrat, Offre", output[0]["cat"]);
        }

        [Fact]
        public async Task Missing_WarnsOrFails_OrCreatesTheItem()
        {
            var logger = new CapturingLogger();
            var warned = await Lookup().ActAsync([V.Entity(("Pays", "Italie"))], V.Context(logger: logger));
            Assert.Equal("Italie", warned[0]["Pays"]);
            Assert.Contains(logger.Warnings, w => w.Contains("Italie"));

            var failed = await Lookup(l => l.FailWhenNotFound = true).ActAsync([V.Entity(("Pays", "Italie"))], V.Context());
            Assert.Equal(SyncResult.Failed, failed[0].SyncResult);

            var created = await Lookup(l => l.CreateMissing = true).ActAsync([V.Entity(("Pays", "Italie"))], V.Context());
            Assert.Equal(4L, created[0]["Pays"]);

            var cleared = await Lookup(l => l.ClearWhenNotFound = true).ActAsync([V.Entity(("Pays", "Espagne"))], V.Context());
            Assert.Null(cleared[0]["Pays"]);
            Assert.NotEqual(SyncResult.Failed, cleared[0].SyncResult);
        }

        [Fact]
        public async Task NoValueListNorProperty_IsAConfigurationError_BeforeConnecting()
        {
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Lookup(l => l.ValueList = null).ActAsync([V.Entity(("Pays", "x"))], V.Context()));
            Assert.Equal(0, _vault.Calls("Connect"));
        }
    }
}
