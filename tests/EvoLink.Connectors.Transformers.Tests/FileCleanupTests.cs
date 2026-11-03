using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Transformers.Tests
{
    public sealed class FileCleanupTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"ortolan-cleanup-{Guid.NewGuid():N}");

        public FileCleanupTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string NewFile(string relative)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
            return path;
        }

        private static Entity Processed(string path, SyncResult result = SyncResult.Created)
            => new() { [FileKeys.FilePath] = path, SyncResult = result };

        [Fact]
        public async Task DeletesOnlyTheFilesOfTheSuccessfulEntities_AndTheEmptyFolders()
        {
            var created = NewFile(@"in\a\doc1.pdf");
            var failed = NewFile(@"in\b\doc2.pdf");
            var skipped = NewFile(@"in\c\doc3.pdf");
            Directory.CreateDirectory(Path.Combine(_root, "in", "keep", "empty"));
            var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.pdf");
            File.WriteAllText(outside, "x");

            try
            {
                var cleanup = new FileCleanupProcessor
                {
                    RootPath = Path.Combine(_root, "in"),
                    DeleteEmptyDirectories = true,
                    KeepDirectoriesPattern = "^keep",
                };

                await cleanup.ActAsync([Processed(created), Processed(failed, SyncResult.Failed), Processed(skipped, SyncResult.Skipped), Processed(outside)], TestSupport.Context());

                Assert.False(File.Exists(created));
                Assert.False(Directory.Exists(Path.Combine(_root, "in", "a")));
                Assert.True(File.Exists(failed));
                Assert.True(File.Exists(skipped));
                Assert.True(File.Exists(outside));
                Assert.True(Directory.Exists(Path.Combine(_root, "in", "keep", "empty")));
                Assert.True(Directory.Exists(Path.Combine(_root, "in")));
            }
            finally
            {
                File.Delete(outside);
            }
        }

        [Fact]
        public async Task MoveTo_ArchivesTheFiles_WithoutOverwriting()
        {
            var archive = Path.Combine(_root, "archive");
            Directory.CreateDirectory(Path.Combine(archive, "x"));
            File.WriteAllText(Path.Combine(archive, "x", "doc.pdf"), "old");
            var path = NewFile(@"in\x\doc.pdf");

            var cleanup = new FileCleanupProcessor { RootPath = Path.Combine(_root, "in"), MoveTo = archive };
            await cleanup.ActAsync([Processed(path, SyncResult.Updated)], TestSupport.Context());

            Assert.False(File.Exists(path));
            Assert.Equal("old", File.ReadAllText(Path.Combine(archive, "x", "doc.pdf")));
            Assert.Equal(@"in\x\doc.pdf", File.ReadAllText(Path.Combine(archive, "x", "doc_1.pdf")));
        }

        [Fact]
        public async Task LockedFile_FailsTheEntityOnlyWhenRequested()
        {
            var path = NewFile("locked.pdf");
            using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            var tolerant = Processed(path);
            await new FileCleanupProcessor { RootPath = _root }.ActAsync([tolerant], TestSupport.Context());
            Assert.Equal(SyncResult.Created, tolerant.SyncResult);

            var strict = Processed(path);
            await new FileCleanupProcessor { RootPath = _root, FailOnError = true }.ActAsync([strict], TestSupport.Context());
            Assert.Equal(SyncResult.Failed, strict.SyncResult);
        }

        [Fact]
        public async Task DeletingEmptyFolders_RequiresARoot()
            => await Assert.ThrowsAsync<OrtolanConfigurationException>(() => new FileCleanupProcessor { DeleteEmptyDirectories = true }.ActAsync([], TestSupport.Context()));
    }
}
