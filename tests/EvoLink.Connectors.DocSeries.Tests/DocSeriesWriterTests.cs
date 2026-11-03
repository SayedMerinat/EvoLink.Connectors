using System.Net;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.DocSeries.Tests
{
    public sealed class DocSeriesWriterTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "evolink-docseries-tests", Guid.NewGuid().ToString("N"));
        private readonly string _file;

        public DocSeriesWriterTests()
        {
            Directory.CreateDirectory(_folder);
            _file = Path.Combine(_folder, "facture.pdf");
            File.WriteAllText(_file, "%PDF");
        }

        public void Dispose()
            => Directory.Delete(_folder, recursive: true);

        private static JobContext Context()
            => new() { JobId = Guid.NewGuid().ToString("N"), ServiceId = "service" };

        private Entity Invoice(params (string Key, object? Value)[] properties)
        {
            var entity = new Entity { ["DocType"] = "Facture", ["FILE-FilePath"] = _file };
            foreach (var (key, value) in properties)
                entity[key] = value;
            return entity;
        }

        private static async Task<List<Entity>> RunAsync(DocSeriesWriter writer, JobContext? context = null, params Entity[] entities)
            => await writer.ActAsync([.. entities], context ?? Context());

        [Fact]
        public async Task CreatesADocumentWithTheFieldsOfTheContentType_AndTheFile()
        {
            var writer = new TestableDocSeriesWriter { ObjectIdProperty = "docId" };

            var entity = Assert.Single(await RunAsync(writer, null, Invoice(("Numero", "F-001"), ("DateFacture", new DateTime(2024, 3, 1)), ("Montant", 1234.5m), ("Inconnu", "ignoré"))));

            var document = Assert.Single(writer.Fake.Documents);
            Assert.Equal("Facture", document.ContentType);
            Assert.Equal(_file, document.FilePath);
            Assert.True(document.AutomaticEmptyFields);
            Assert.Equal(new Dictionary<string, string> { ["Numero"] = "F-001", ["DateFacture"] = "01.03.2024", ["Montant"] = "1234.5" }, document.Fields);
            Assert.Equal(SyncResult.Created, entity.SyncResult);
            Assert.Equal("1001", entity.Metadata[DocSeriesWriter.ObjectIdMetadata]);
            Assert.Equal(1001L, entity["docId"]);
        }

        [Fact]
        public async Task FieldMapping_RenamesProperties_AndUnknownMappedFieldFailsTheEntity()
        {
            var writer = new TestableDocSeriesWriter
            {
                ContentType = "facture",
                Fields = new() { ["invoiceNumber"] = "Numero", ["date"] = "Date de facture" },
                OnlyMappedFields = true,
                DateFormat = "yyyy-MM-dd",
            };

            var ok = new Entity { ["invoiceNumber"] = "F-2", ["date"] = new DateTime(2024, 1, 31), ["Montant"] = "10", ["FILE-FilePath"] = _file };
            await RunAsync(writer, null, ok);

            var document = Assert.Single(writer.Fake.Documents);
            Assert.Equal(new Dictionary<string, string> { ["Numero"] = "F-2", ["DateFacture"] = "2024-01-31" }, document.Fields);

            writer.Fields["other"] = "Absent";
            var failed = Assert.Single(await RunAsync(writer, null, new Entity { ["other"] = "x", ["FILE-FilePath"] = _file }));
            Assert.True(failed.IsFailed);
            Assert.Contains("Absent", failed.Error);
        }

        [Fact]
        public async Task ContentType_CanBeGivenById_AndAnUnknownOneFailsTheEntity()
        {
            var writer = new TestableDocSeriesWriter();

            var results = await RunAsync(writer, null, Invoice(("DocType", "1"), ("Numero", "A")), Invoice(("DocType", "Devis")), Invoice(("DocType", null)));

            Assert.Equal(SyncResult.Created, results[0].SyncResult);
            Assert.True(results[1].IsFailed);
            Assert.Contains("Devis", results[1].Error);
            Assert.True(results[2].IsFailed);
            Assert.Single(writer.Fake.Documents);
        }

        [Fact]
        public async Task ListValues_AreMatchedWithTheConfiguredComparison()
        {
            var equals = new TestableDocSeriesWriter { ListItemIgnoreCase = true };
            await RunAsync(equals, null, Invoice(("Fournisseur", "acme sa"), ("Pays", "SUISSE"), ("Statut", "payé")));

            Assert.Equal(new Dictionary<string, string> { ["Fournisseur"] = "ACME SA", ["Pays"] = "Suisse", ["Statut"] = "Payé" }, Assert.Single(equals.Fake.Documents).Fields);

            var contains = new TestableDocSeriesWriter { ListItemComparison = ListItemComparison.Contains };
            await RunAsync(contains, null, Invoice(("Fournisseur", "Globex"), ("Pays", "Fra")));

            Assert.Equal(new Dictionary<string, string> { ["Fournisseur"] = "Globex Sàrl", ["Pays"] = "France" }, Assert.Single(contains.Fake.Documents).Fields);
        }

        [Fact]
        public async Task MissingListItem_IsReportedAndTheFieldLeftEmpty_OrFailsTheEntity()
        {
            var lenient = new TestableDocSeriesWriter();
            var context = Context();
            var entity = Assert.Single(await RunAsync(lenient, context, Invoice(("Numero", "1"), ("Fournisseur", "Initech"), ("Statut", "Annulé"))));

            Assert.Equal(SyncResult.Created, entity.SyncResult);
            Assert.Equal(new Dictionary<string, string> { ["Numero"] = "1" }, Assert.Single(lenient.Fake.Documents).Fields);
            Assert.Equal(2, context.Report.Warnings);
            Assert.Empty(lenient.Fake.CreatedItems);

            var strict = new TestableDocSeriesWriter { FailWhenNotFound = true };
            Assert.True(Assert.Single(await RunAsync(strict, null, Invoice(("Pays", "Italie")))).IsFailed);
            Assert.Empty(strict.Fake.Documents);
        }

        [Fact]
        public async Task CreateListItemIfMissing_CreatesTheItemOnceInInternalTablesOnly()
        {
            var writer = new TestableDocSeriesWriter { CreateListItemIfMissing = true, MaxDegreeOfParallelism = 4 };
            var context = Context();

            var results = await RunAsync(writer, context,
                Invoice(("Numero", "1"), ("Fournisseur", "Initech")),
                Invoice(("Numero", "2"), ("Fournisseur", "Initech")),
                Invoice(("Numero", "3"), ("Fournisseur", "Initech"), ("Pays", "Italie")));

            Assert.All(results, e => Assert.Equal(SyncResult.Created, e.SyncResult));
            var created = Assert.Single(writer.Fake.CreatedItems);
            Assert.Equal((12, "Nom", "Initech"), created);
            Assert.All(writer.Fake.Documents, d => Assert.Equal("Initech", d.Fields["Fournisseur"]));
            Assert.DoesNotContain(writer.Fake.Documents, d => d.Fields.ContainsKey("Pays"));
            Assert.Equal(1, context.Report.Warnings);
        }

        [Fact]
        public async Task ListsAreReadOncePerJob()
        {
            var writer = new TestableDocSeriesWriter();

            await RunAsync(writer, null, Invoice(("Fournisseur", "ACME SA")), Invoice(("Fournisseur", "Globex Sàrl")), Invoice(("Pays", "France")));

            Assert.Equal(2, writer.Fake.ListReads);
            Assert.Equal(3, writer.Fake.Documents.Count);
        }

        [Fact]
        public async Task File_IsRequiredByDefault_AndMustExist()
        {
            var noFile = new Entity { ["DocType"] = "Facture", ["Numero"] = "1" };
            var missingFile = Invoice(("FILE-FilePath", Path.Combine(_folder, "absent.pdf")));

            var strict = new TestableDocSeriesWriter();
            var results = await RunAsync(strict, null, noFile.Clone(), missingFile);
            Assert.All(results, e => Assert.True(e.IsFailed));
            Assert.Empty(strict.Fake.Documents);

            var lenient = new TestableDocSeriesWriter { RequireFile = false };
            Assert.Equal(SyncResult.Created, Assert.Single(await RunAsync(lenient, null, noFile)).SyncResult);
            Assert.Null(Assert.Single(lenient.Fake.Documents).FilePath);
        }

        [Fact]
        public async Task UnreachableServer_IsATargetUnavailability()
        {
            var atStart = new TestableDocSeriesWriter();
            atStart.Fake.Failure = new HttpRequestException("Connexion refusée");
            await Assert.ThrowsAsync<TargetUnavailableException>(() => RunAsync(atStart, null, Invoice()));

            var duringCreation = new TestableDocSeriesWriter();
            duringCreation.Fake.CreationFailure = new HttpRequestException("Timeout", null, HttpStatusCode.ServiceUnavailable);
            await Assert.ThrowsAsync<TargetUnavailableException>(() => RunAsync(duringCreation, null, Invoice()));
        }

        [Fact]
        public async Task RefusedRequest_FailsOnlyTheEntity()
        {
            var writer = new TestableDocSeriesWriter();
            writer.Fake.CreationFailure = new DocSeriesRequestException("Valeur refusée");

            var entity = Assert.Single(await RunAsync(writer, null, Invoice(("Numero", "1"))));

            Assert.True(entity.IsFailed);
            Assert.Equal("Valeur refusée", entity.Error);
        }

        [Theory]
        [InlineData("", "user", "pwd")]
        [InlineData("https://docseries.test", "", "pwd")]
        [InlineData("https://docseries.test", "user", "")]
        [InlineData("docseries.test", "user", "pwd")]
        public async Task InvalidConfiguration_IsAConfigurationError(string host, string user, string password)
        {
            var writer = new TestableDocSeriesWriter { Host = host, Username = user, Password = password };

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => RunAsync(writer, null, Invoice()));
        }

        [Fact]
        public void Configuration_UsesTheAlias_AndThePasswordIsSensitive()
        {
            var registry = new TypeRegistry().RegisterAssembly(typeof(DocSeriesWriter).Assembly);

            var module = OrtolanJson.ToConfiguration<IModule>(JToken.Parse("""
                { "$type": "docseries-writer", "host": "https://docseries.test", "username": "u", "password": "@vault:docseries",
                  "listItemComparison": "Contains", "createListItemIfMissing": true, "fields": { "numero": "Numero" } }
                """), registry, "de test");

            var writer = Assert.IsType<DocSeriesWriter>(module);
            Assert.Equal(ListItemComparison.Contains, writer.ListItemComparison);
            Assert.True(writer.CreateListItemIfMissing);
            Assert.Equal("Numero", writer.Fields["numero"]);
            Assert.True(TypeRegistry.IsSensitive(typeof(DocSeriesWriter).GetProperty(nameof(DocSeriesWriter.Password))!));
            Assert.NotNull(typeof(DocSeriesWriter).GetProperty(nameof(DocSeriesWriter.Password))!.GetCustomAttribute<SensitiveAttribute>());
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest, true)]
        [InlineData(HttpStatusCode.NotFound, true)]
        [InlineData(HttpStatusCode.InternalServerError, true)]
        [InlineData(HttpStatusCode.Unauthorized, false)]
        [InlineData(HttpStatusCode.Forbidden, false)]
        [InlineData(HttpStatusCode.RequestTimeout, false)]
        [InlineData(HttpStatusCode.TooManyRequests, false)]
        [InlineData(HttpStatusCode.BadGateway, false)]
        [InlineData(HttpStatusCode.ServiceUnavailable, false)]
        public void RealGateway_ClassifiesHttpErrors(HttpStatusCode status, bool refused)
            => Assert.Equal(refused, DocSeriesGateway.IsRefusedRequest(new HttpRequestException("x", null, status)));

        [Fact]
        public void RealGateway_NetworkErrorWithoutStatus_IsAnAccessFailure()
            => Assert.False(DocSeriesGateway.IsRefusedRequest(new HttpRequestException("Nom d'hôte inconnu")));
    }
}
