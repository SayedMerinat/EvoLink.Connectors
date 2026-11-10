using System.ComponentModel;
using System.Reflection;
using EvoLink.Odoo.Client;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Odoo.Tests
{
    public class OdooConfigurationTests
    {
        private static readonly TypeRegistry Registry = new TypeRegistry().RegisterAssembly(typeof(OdooSynchroniser).Assembly);

        [Theory]
        [InlineData("odoo-provider", typeof(OdooProvider), "Provider")]
        [InlineData("odoo-synchroniser", typeof(OdooSynchroniser), "Synchroniser")]
        [InlineData("odoo-id-lookup", typeof(OdooIdLookupTransformer), "Transformer")]
        public void Modules_AreRegisteredWithTheirAlias(string alias, Type type, string category)
        {
            Assert.True(Registry.TryGetType(alias, out var registered));
            Assert.Equal(type, registered);
            Assert.Equal(category, Registry.GetCategory(type));
        }

        [Theory]
        [InlineData(typeof(OdooProvider))]
        [InlineData(typeof(OdooSynchroniser))]
        [InlineData(typeof(OdooIdLookupTransformer))]
        public void Modules_DescribeEveryPropertyAndProtectTheApiKey(Type type)
        {
            Assert.NotNull(type.GetCustomAttribute<DescriptionAttribute>());
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes));

            var properties = TypeRegistry.GetConfigurableProperties(type).ToList();
            Assert.All(properties, p => Assert.False(string.IsNullOrWhiteSpace(p.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{type.Name}.{p.Name} n'a pas de description."));
            Assert.Equal(["ApiKey"], properties.Where(TypeRegistry.IsSensitive).Select(p => p.Name));
        }

        [Fact]
        public void Protector_EncryptsTheApiKeyAndResolvesTheVaultReferences()
        {
            var cipher = new SecretCipher("tests-passphrase");
            var vault = new CredentialVault(cipher);
            vault.Set("odoo", JObject.Parse("""{ "ApiKey": "from-vault" }"""));
            var protector = new ConfigurationProtector(cipher, Registry, vault);

            var stages = JArray.Parse("""
                [ { "name": "s", "modules": [
                    { "$type": "odoo-provider", "url": "https://odoo.test", "model": "res.partner", "apiKey": "clear-key-1" },
                    { "$type": "odoo-synchroniser", "url": "https://odoo.test", "model": "res.partner", "apiKey": "clear-key-2" },
                    { "$type": "odoo-id-lookup", "url": "https://odoo.test", "model": "res.partner", "apiKey": "@vault:odoo#ApiKey" } ] } ]
                """);

            var protectedStages = protector.Protect(stages);

            Assert.DoesNotContain("clear-key", protectedStages.ToString());
            Assert.Contains("@vault:odoo#ApiKey", protectedStages.ToString());

            var revealed = protector.Reveal(protectedStages);
            Assert.Equal("clear-key-1", revealed.SelectToken("[0].modules[0].apiKey")!.Value<string>());
            Assert.Equal("clear-key-2", revealed.SelectToken("[0].modules[1].apiKey")!.Value<string>());
            Assert.Equal("from-vault", revealed.SelectToken("[0].modules[2].apiKey")!.Value<string>());
        }

        [Fact]
        public void Connection_NeverPrintsTheApiKey()
        {
            var connection = new OdooConnection { Url = "https://odoo.test/", Database = "odoo", Username = "ortolan", ApiKey = "very-secret" };

            Assert.DoesNotContain("very-secret", connection.ToString());
            Assert.Equal("https://odoo.test/jsonrpc", connection.Endpoint.ToString());
        }

        [Theory]
        [InlineData("char", false, null)]
        [InlineData("boolean", false, false)]
        [InlineData("char", "x", "x")]
        [InlineData("integer", 3L, 3L)]
        public void OdooValues_AreMadeComparable(string type, object? value, object? expected)
            => Assert.Equal(expected, OdooValues.FromOdoo(value, type));

        [Fact]
        public void OdooRelations_AreMadeComparable()
        {
            Assert.Equal(7L, OdooValues.FromOdoo(new List<object?> { 7L, "EvoLink SA" }, "many2one"));
            Assert.Equal(new List<object?> { 1L, 3L }, OdooValues.FromOdoo(new List<object?> { 3L, 1L }, "many2many"));
            Assert.Equal(12.5m, OdooValues.FromOdoo(12.5, "float"));
        }

        [Theory]
        [InlineData("float", "1'234.50", "1234.5")]
        [InlineData("float", "12,50", "12.5")]
        [InlineData("float", null, "0")]
        [InlineData("integer", "42", "42")]
        [InlineData("boolean", "oui", "true")]
        [InlineData("boolean", null, "false")]
        [InlineData("date", "17.05.1990", "1990-05-17")]
        [InlineData("datetime", "2024-07-01 10:30:00", "2024-07-01 08:30:00")]
        [InlineData("datetime", "2024-07-01T10:30:00Z", "2024-07-01 10:30:00")]
        [InlineData("char", "", "")]
        [InlineData("many2one", "7", "7")]
        [InlineData("many2many", "[3, 1]", "[1,3]")]
        public async Task SourceValues_AreConvertedToTheOdooRepresentation(string type, object? value, string expected)
        {
            var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
            var comparable = await OdooValues.ToComparableAsync(value, new OdooFieldInfo("f", type, "res.partner"), zurich, null);

            Assert.Equal(expected, Ortolan.Core.Sync.SyncComparer.Canonical(comparable));
        }

        [Fact]
        public async Task SourceAndOdooValues_HaveTheSameCanonicalForm()
        {
            var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
            var field = new OdooFieldInfo("credit_limit", "monetary");

            Assert.Equal(
                Ortolan.Core.Sync.SyncComparer.Canonical(OdooValues.FromOdoo(1500.5, "monetary")),
                Ortolan.Core.Sync.SyncComparer.Canonical(await OdooValues.ToComparableAsync("1500.50", field, zurich, null)));
        }

        [Fact]
        public async Task InvalidSourceValue_IsAFormatException()
        {
            var ex = await Assert.ThrowsAsync<FormatException>(() => OdooValues.ToComparableAsync("abc", new OdooFieldInfo("employees", "integer"), TimeZoneInfo.Utc, null));
            Assert.Contains("employees", ex.Message);
        }

        [Fact]
        public void Payload_UsesFalseForEmptyValuesAndTheReplaceCommandForMany2Many()
        {
            Assert.Equal(false, OdooValues.ToPayload(null, new OdooFieldInfo("email", "char")));
            Assert.Equal(false, OdooValues.ToPayload(null, new OdooFieldInfo("parent_id", "many2one")));

            var command = Assert.IsType<List<object?>>(OdooValues.ToPayload(new List<object?> { 1L, 2L }, new OdooFieldInfo("category_id", "many2many")));
            Assert.Equal("[[6,0,[1,2]]]", OdooJson.ToJson(command).ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
