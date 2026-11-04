using System.Reflection;
using System.Runtime.InteropServices;
using EvoLink.Connectors.MFiles.Gateway;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.MFiles.Tests
{
    public class ConnectionSettingsTests
    {
        [Theory]
        [InlineData("ncacn_ip_tcp", 2266, false)]
        [InlineData("grpc", 7766, false)]
        [InlineData("ncacn_http", 80, false)]
        [InlineData("ncacn_ip_tcp", 7766, true)]
        [InlineData("grpc", 2266, true)]
        [InlineData("ncacn_ip_tcp", 1234, true)]
        public void EndpointHint_FlagsAPortThatDoesntMatchTheProtocol(string protocol, int port, bool expected)
        {
            var settings = new MFilesConnectionSettings("srv", protocol, port, "vault", MFilesAuthType.CurrentWindowsUser, null, null, null);

            Assert.Equal(expected, settings.EndpointHint.Length > 0);
        }

        [Fact]
        public void EndpointHint_GrpcPortWithTcp_PointsToTheStandardPort()
            => Assert.Contains("2266", new MFilesConnectionSettings("srv", "ncacn_ip_tcp", 7766, "vault", MFilesAuthType.CurrentWindowsUser, null, null, null).EndpointHint);
    }

    public class SessionCacheTests
    {
        [Fact]
        public void ValueLists_AreLoadedOncePerSession_EvenInParallel()
        {
            var vault = V.CreateVault();
            vault.ItemsLoadDelayMs = 50;
            var session = new MFilesSession(vault);
            var names = new[] { "Suisse", "France", "Allemagne", "Contrat", "Offre" };

            Parallel.For(0, 500, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                var name = names[i % names.Length];
                var list = i % names.Length < 3 ? V.CountryList : V.CategoryList;
                Assert.Equal(name, session.RequireItem(list, name).Name);
            });

            Assert.Equal(1, vault.Calls($"GetValueListItems:{V.CountryList}"));
            Assert.Equal(1, vault.Calls($"GetValueListItems:{V.CategoryList}"));
        }

        [Fact]
        public void MissingItem_IsCreatedOnce_WhenRequestedInParallel()
        {
            var vault = V.CreateVault();
            var session = new MFilesSession(vault);
            var ids = new System.Collections.Concurrent.ConcurrentBag<int>();

            Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ => ids.Add(session.RequireItem(V.CountryList, "Italie", createMissing: true).Id));

            Assert.Equal(1, vault.Calls("AddValueListItem"));
            Assert.Single(ids.Distinct());
            Assert.Single(vault.Items[V.CountryList], i => i.Name == "Italie");
        }

        [Fact]
        public async Task Synchroniser_InParallel_CreatesAMissingItemOnce()
        {
            var vault = V.CreateVault();
            vault.ItemsLoadDelayMs = 20;
            var synchroniser = new TestSynchroniser(vault) { Vault = "Test", Class = "Facture", CreateMissingValueListItems = true, MaxDegreeOfParallelism = 8 };
            var entities = Enumerable.Range(0, 100).Select(i => V.Entity(("Numéro", $"F-{i}"), ("Pays", "Italie"), ("Catégories", "Contrat;Nouvelle"))).ToList();
            var context = V.Context();

            await synchroniser.ActAsync(entities, context);

            Assert.Equal(100, context.Report.Created);
            Assert.Equal(2, vault.Calls("AddValueListItem"));
            Assert.Equal(1, vault.Calls($"GetValueListItems:{V.CountryList}"));
        }

        [Fact]
        public void Caches_BelongToTheSession()
        {
            var vault = V.CreateVault();

            new MFilesSession(vault).RequireItem(V.CountryList, "Suisse");
            vault.Items[V.CountryList].Add(new MFilesValueListItem(9, "Autriche"));

            // A new session (new job) sees the new item: nothing is shared between jobs.
            Assert.Equal(9, new MFilesSession(vault).RequireItem(V.CountryList, "Autriche").Id);
            Assert.Equal(2, vault.Calls($"GetValueListItems:{V.CountryList}"));
        }

        [Fact]
        public void FailedLoad_IsNotCached()
        {
            var vault = V.CreateVault();
            var fail = true;
            vault.FailOn = op => fail && op == "GetValueListItems" ? new MFilesConnectionException("coupure") : null;
            var session = new MFilesSession(vault);

            Assert.Throws<MFilesConnectionException>(() => session.FindItem(V.CountryList, "Suisse"));
            fail = false;

            Assert.Equal(1, session.RequireItem(V.CountryList, "Suisse").Id);
        }

        [Fact]
        public void AmbiguousItemName_RequiresTheId()
        {
            var vault = V.CreateVault();
            vault.Items[V.CategoryList].Add(new MFilesValueListItem(4, "Contrat"));
            var session = new MFilesSession(vault);

            var error = Assert.Throws<InvalidOperationException>(() => session.FindItem(V.CategoryList, "Contrat"));
            Assert.Contains("4", error.Message);
            Assert.Equal(4, session.RequireItem(V.CategoryList, 4).Id);
        }

        [Theory]
        [InlineData(MFilesDataType.Integer, "12a")]
        [InlineData(MFilesDataType.Integer, "3000000000")]
        [InlineData(MFilesDataType.Floating, "abc")]
        [InlineData(MFilesDataType.Date, "31.02.2024")]
        [InlineData(MFilesDataType.Boolean, "peut-être")]
        public void InvalidValue_ThrowsAFormatException(MFilesDataType dataType, string value)
        {
            var session = new MFilesSession(V.CreateVault());
            var definition = new MFilesPropertyDefInfo(1, "P", dataType, null, false);

            Assert.Throws<FormatException>(() => session.ToPropertyValue(definition, value));
        }
    }

    public class ErrorClassificationTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();

        [Fact]
        public async Task ConnectionFailure_IsAnUnavailability_ForEveryModule()
        {
            _vault.ConnectFailure = new MFilesConnectionException("Serveur arrêté");
            var entity = () => V.Entity(("Numéro", "F-1"), ("Pays", "Suisse"));

            await Assert.ThrowsAsync<TargetUnavailableException>(() => new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture" }.ActAsync([entity()], V.Context()));
            await Assert.ThrowsAsync<SourceUnavailableException>(() => new TestProvider(_vault) { Vault = "Test", Class = "Facture" }.ActAsync([], V.Context()));
            await Assert.ThrowsAsync<SourceUnavailableException>(() => new TestValueLookup(_vault) { Vault = "Test", ValueList = "Pays", SourceProperty = "Pays" }.ActAsync([entity()], V.Context()));
            await Assert.ThrowsAsync<SourceUnavailableException>(() => new TestUserLookup(_vault) { Vault = "Test", Class = "Contact externe", SourceProperty = "Pays", MatchField = "Login externe" }.ActAsync([entity()], V.Context()));

            // The gateway is released even when the connection fails.
            Assert.Equal(4, _vault.Calls("Dispose"));
        }

        [Fact]
        public async Task ConnectionLostDuringAnOperation_StopsTheJob()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.Amount, 1.0));
            _vault.FailOn = op => op == "Update" ? new COMException("RPC server unavailable", unchecked((int)0x800706BA)) : null;

            var error = await Assert.ThrowsAsync<TargetUnavailableException>(() =>
                new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture" }.ActAsync([V.Entity(("Numéro", "F-1"), ("Montant", 2))], V.Context()));

            Assert.IsType<COMException>(error.InnerException);
            Assert.Equal(1, _vault.Calls("Disconnect"));
        }

        [Fact]
        public async Task RefusedOperation_FailsTheEntity_AndTheJobContinues()
        {
            _vault.AddObject(V.InvoiceClass, (V.Number, "F-1"), (V.Amount, 1.0));
            _vault.FailOn = op => op == "Update" ? new MFilesOperationException("Accès refusé") : null;
            var context = V.Context();

            var output = await new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture" }
                .ActAsync([V.Entity(("Numéro", "F-1"), ("Montant", 2)), V.Entity(("Numéro", "F-2"))], context);

            Assert.Equal(SyncResult.Failed, output[0].SyncResult);
            Assert.Equal("Accès refusé", output[0].Error);
            Assert.Equal(SyncResult.Created, output[1].SyncResult);
        }

        [Fact]
        public async Task ConnectionLostInATransformer_StopsTheJob()
        {
            _vault.FailOn = op => op == "Search" ? new MFilesConnectionException("coupure") : null;
            var lookup = new TestUserLookup(_vault) { Vault = "Test", Class = "Contact externe", SourceProperty = "a", MatchField = "Login externe" };

            await Assert.ThrowsAsync<SourceUnavailableException>(() => lookup.ActAsync([V.Entity(("a", "x"))], V.Context()));
        }

        [Theory]
        [InlineData(0x800706BA, true)]  // RPC server unavailable
        [InlineData(0x800706BE, true)]  // RPC call failed
        [InlineData(0x80010108, true)]  // object disconnected
        [InlineData(0x8004000B, false)] // M-Files "not found"
        [InlineData(0x80040031, false)] // M-Files validation error
        public void ComErrors_AreClassified(uint hresult, bool connection)
            => Assert.Equal(connection, MFilesErrors.IsConnectionFailure(new COMException("x", unchecked((int)hresult))));

        [Fact]
        public void ReadableMessage_KeepsTheFirstLine()
            => Assert.Equal("Objet introuvable.", MFilesErrors.ReadableMessage(new COMException("Objet introuvable.\r\n\r\nCMFServerException::Find (0x8004000B)\nstack...")));
    }

    public class ConfigurationTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();

        [Fact]
        public async Task MissingVault_FailsBeforeConnecting()
        {
            var error = await Assert.ThrowsAsync<OrtolanConfigurationException>(() =>
                new TestSynchroniser(_vault) { Class = "Facture" }.ActAsync([V.Entity(("Numéro", "1"))], V.Context()));

            Assert.Contains("vault", error.Message);
            Assert.Equal(0, _vault.Calls("Connect"));
        }

        [Fact]
        public async Task MissingClass_FailsBeforeConnecting()
        {
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new TestProvider(_vault) { Vault = "Test" }.ActAsync([], V.Context()));
            Assert.Equal(0, _vault.Calls("Connect"));
        }

        [Theory]
        [InlineData(MFilesAuthType.SpecificWindowsUser)]
        [InlineData(MFilesAuthType.MFilesUser)]
        public async Task SpecificAccount_RequiresAUsername(MFilesAuthType authType)
        {
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() =>
                new TestProvider(_vault) { Vault = "Test", Class = "Facture", AuthType = authType }.ActAsync([], V.Context()));
        }

        [Fact]
        public async Task UnknownClass_IsAConfigurationError()
        {
            var error = await Assert.ThrowsAsync<OrtolanConfigurationException>(() =>
                new TestSynchroniser(_vault) { Vault = "Test", Class = "Inexistante" }.ActAsync([V.Entity(("Numéro", "1"))], V.Context()));

            Assert.Contains("Inexistante", error.Message);
            Assert.Equal(1, _vault.Calls("Disconnect"));
        }

        [Fact]
        public async Task UnknownMappedProperty_IsAConfigurationError()
        {
            var synchroniser = new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture", Fields = new() { ["a"] = "PD.Inexistante" } };

            var error = await Assert.ThrowsAsync<OrtolanConfigurationException>(() => synchroniser.ActAsync([V.Entity(("Numéro", "1"))], V.Context()));
            Assert.Contains("PD.Inexistante", error.Message);
        }

        [Fact]
        public async Task MissingKeyFields_IsAConfigurationError()
        {
            var context = V.Context();
            context.Sync.KeyFields.Clear();

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() =>
                new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture" }.ActAsync([V.Entity(("Numéro", "1"))], context));
        }

        [Fact]
        public async Task ConnectionSettings_HaveSensibleDefaults()
        {
            await new TestProvider(_vault) { Vault = " Test " , Class = "Facture" }.ActAsync([], V.Context());

            var settings = _vault.Settings!;
            Assert.Equal("localhost", settings.Server);
            Assert.Equal("ncacn_ip_tcp", settings.Protocol);
            Assert.Equal(2266, settings.Port);
            Assert.Equal("Test", settings.Vault);
            Assert.Equal(MFilesAuthType.CurrentWindowsUser, settings.AuthType);
        }
    }

    public class SecretTests
    {
        private static readonly Type[] Modules = [typeof(MFilesProvider), typeof(MFilesSynchroniser), typeof(MFilesUserLookup), typeof(MFilesValueLookup)];

        public static TheoryData<Type> ModuleTypes => new(Modules);

        [Theory]
        [MemberData(nameof(ModuleTypes))]
        public void Password_IsSensitive_AndOnlyThePassword(Type type)
        {
            var sensitive = TypeRegistry.GetConfigurableProperties(type).Where(TypeRegistry.IsSensitive).Select(p => p.Name);

            Assert.Equal(["Password"], sensitive);
        }

        [Fact]
        public void Modules_AreRegisteredWithTheirAliases()
        {
            var registry = new TypeRegistry().RegisterAssembly(typeof(MFilesSynchroniser).Assembly);

            Assert.True(registry.TryGetType("mfiles-provider", out var provider) && provider == typeof(MFilesProvider));
            Assert.True(registry.TryGetType("mfiles-synchroniser", out var synchroniser) && synchroniser == typeof(MFilesSynchroniser));
            Assert.True(registry.TryGetType("mfiles-user-lookup", out var user) && user == typeof(MFilesUserLookup));
            Assert.True(registry.TryGetType("mfiles-value-lookup", out var value) && value == typeof(MFilesValueLookup));
            Assert.All(registry.Types.Where(t => t.Alias.StartsWith("mfiles-")), t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        }

        [Fact]
        public void EveryConfigurableProperty_HasAFrenchDescription()
        {
            foreach (var type in Modules)
            {
                var undocumented = TypeRegistry.GetConfigurableProperties(type)
                    .Where(p => p.DeclaringType!.Assembly == type.Assembly && p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() == null)
                    .Select(p => $"{type.Name}.{p.Name}");

                Assert.Empty(undocumented);
            }
        }

        [Fact]
        public void Password_IsEncryptedInThePersistedConfiguration()
        {
            var registry = new TypeRegistry().RegisterAssembly(typeof(MFilesSynchroniser).Assembly);
            var protector = new ConfigurationProtector(new SecretCipher("test-passphrase"), registry, null);
            var configuration = JObject.Parse("""{ "$type": "mfiles-synchroniser", "vault": "Test", "class": "Facture", "username": "svc", "password": "Secret!42" }""");

            var protectedConfiguration = protector.Protect(configuration);
            var stored = protectedConfiguration["password"]!.Value<string>()!;

            Assert.StartsWith(ConfigurationProtector.EncryptedPrefix, stored);
            Assert.DoesNotContain("Secret!42", protectedConfiguration.ToString());
            Assert.Equal("Secret!42", protector.Reveal(protectedConfiguration)["password"]!.Value<string>());

            var module = OrtolanJson.ToConfiguration<Ortolan.Core.Modules.IModule>(protector.Reveal(protectedConfiguration), registry, "test");
            Assert.Equal("Secret!42", Assert.IsType<MFilesSynchroniser>(module).Password);
        }

        [Fact]
        public async Task Password_NeverAppearsInTheLogsNorTheErrors()
        {
            var vault = V.CreateVault();
            vault.ConnectFailure = new MFilesConnectionException("Authentification refusée");
            var logger = new CapturingLogger();
            var provider = new TestProvider(vault) { Vault = "Test", Class = "Facture", AuthType = MFilesAuthType.MFilesUser, Username = "svc", Password = "Secret!42" };

            var error = await Assert.ThrowsAsync<SourceUnavailableException>(() => provider.ActAsync([], V.Context(logger: logger)));

            Assert.DoesNotContain("Secret!42", error.ToString());
            Assert.DoesNotContain(logger.Messages, m => m.Message.Contains("Secret!42"));
            Assert.DoesNotContain("Secret!42", vault.Settings!.ToString());
            Assert.Equal("Secret!42", vault.Settings.Password);
        }
    }
}
