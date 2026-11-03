using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.MFiles.Tests
{
    /// <summary>
    /// Features of EvoBus ported for the migration: multi-file documents grouped by a property (BatchFrom) and object lookup.
    /// </summary>
    public class MigrationFeaturesTests
    {
        private readonly FakeMFilesGateway _vault = V.CreateVault();

        private TestSynchroniser Synchroniser(Action<MFilesSynchroniser>? configure = null)
        {
            var synchroniser = new TestSynchroniser(_vault) { Vault = "Test", Class = "Facture" };
            configure?.Invoke(synchroniser);
            return synchroniser;
        }

        [Fact]
        public async Task GroupFilesBy_CreatesOneMultiFileDocumentPerGroup()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"mfiles-group-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string NewFile(string name)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, name);
                return path;
            }

            try
            {
                var first = V.Entity(("Numéro", "F-1"), ("Nom ou titre", "Dossier F-1"), ("BatchID", "lot-1"), (MFilesKeys.FilePath, NewFile("a.pdf")));
                var second = V.Entity(("Numéro", "F-1"), ("BatchID", "lot-1"), (MFilesKeys.FilePath, NewFile("b.docx")));
                var third = V.Entity(("Numéro", "F-1"), ("BatchID", "lot-1"), (MFilesKeys.FilePath, NewFile("c.xlsx")));
                var alone = V.Entity(("Numéro", "F-2"), ("BatchID", "lot-2"), (MFilesKeys.FilePath, NewFile("d.pdf")));

                var output = await Synchroniser(s => s.GroupFilesBy = "BatchID").ActAsync([first, second, third, alone], V.Context());

                Assert.Equal(2, _vault.Creates.Count);
                var grouped = _vault.Creates.Single(r => r.AllFiles.Count == 3);
                Assert.Equal(["a.pdf", "b.docx", "c.xlsx"], grouped.AllFiles.Select(f => Path.GetFileName(f.Path)));
                Assert.Equal("Dossier F-1", grouped.Values.Single(v => v.PropertyDefId == V.NameOrTitle).Value);

                var single = _vault.Creates.Single(r => r.AllFiles.Count == 1);
                Assert.Equal("d", single.Values.Single(v => v.PropertyDefId == V.NameOrTitle).Value);

                Assert.Equal(4, output.Count);
                Assert.All(output, e => Assert.Equal(SyncResult.Created, e.SyncResult));
                Assert.Equal(first.Metadata[MFilesKeys.MetadataObjectId], third.Metadata[MFilesKeys.MetadataObjectId]);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task ObjectLookup_WritesTheObjectId_AndRefusesAUserField()
        {
            var contact = _vault.AddObject(V.ContactClass, (V.ExternalLogin, "j.doe@client.ch"));
            var lookup = new TestObjectLookup(_vault) { Vault = "Test", Class = "Contact externe", SourceProperty = "Contact", MatchField = "Login externe", IdProperty = "ContactId" };

            var output = await lookup.ActAsync([V.Entity(("Contact", "j.doe@client.ch"))], V.Context());

            Assert.Equal((long)contact.Id, output[0]["ContactId"]);
            Assert.False(output[0].Has(MFilesKeys.CreatedBy));

            lookup.UserField = "Compte";
            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => lookup.ActAsync([V.Entity(("Contact", "x"))], V.Context()));
        }

        public sealed class TestObjectLookup(FakeMFilesGateway fake) : MFilesObjectLookup
        {
            protected override IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
            {
                fake.Settings = settings;
                return fake;
            }
        }
    }
}
