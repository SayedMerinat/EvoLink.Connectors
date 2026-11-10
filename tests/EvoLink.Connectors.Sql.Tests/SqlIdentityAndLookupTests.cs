using Microsoft.Data.Sqlite;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.Sql.Tests
{
    /// <summary>
    /// Generated identifiers (identityColumn) and foreign keys (sql-lookup): the "Produit then Article" scenario, where an article
    /// references the local ID of a product created in the same job.
    /// </summary>
    public sealed class SqlIdentityAndLookupTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"ortolan-sql-id-{Guid.NewGuid():N}.db");
        private string ConnectionString => $"Data Source={_path};Pooling=False";

        public SqlIdentityAndLookupTests()
        {
            Execute("""
                CREATE TABLE Produit (
                    ID INTEGER PRIMARY KEY AUTOINCREMENT,
                    OdooId INTEGER NOT NULL UNIQUE,
                    Nom TEXT NOT NULL);
                CREATE TABLE Article (
                    ID INTEGER PRIMARY KEY AUTOINCREMENT,
                    OdooId INTEGER NOT NULL UNIQUE,
                    Code TEXT NOT NULL,
                    ProduitId INTEGER NOT NULL REFERENCES Produit(ID));
                -- An existing product, so that the generated IDs differ from the Odoo identifiers.
                INSERT INTO Produit (OdooId, Nom) VALUES (999, 'Ancien');
                """);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_path);
        }

        private void Execute(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; " + sql;
            command.ExecuteNonQuery();
        }

        private List<Dictionary<string, object?>> Rows(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();

            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));

            return rows;
        }

        private static JobContext Context()
            => new()
            {
                JobId = Guid.NewGuid().ToString("N"),
                ServiceId = "sql-identity-tests",
                Mode = SyncMode.Full,
                Sync = new SyncOptions { KeyFields = ["OdooId"] },
            };

        private SqlSynchroniserModule Produits(Action<SqlSynchroniserModule>? configure = null)
        {
            var module = new SqlSynchroniserModule
            {
                Engine = SqlEngine.Sqlite,
                ConnectionString = ConnectionString,
                Table = "Produit",
                IdentityColumn = "ID",
                IdentityProperty = "ProduitId",
            };

            configure?.Invoke(module);
            return module;
        }

        private SqlSynchroniserModule Articles()
            => new()
            {
                Engine = SqlEngine.Sqlite,
                ConnectionString = ConnectionString,
                Table = "Article",
                IdentityColumn = "ID",
                OnlyMappedFields = true,
                Fields = new Dictionary<string, string> { ["OdooId"] = "OdooId", ["Code"] = "Code", ["ProduitId"] = "ProduitId" },
            };

        private SqlLookupTransformer ProduitLookup(bool failWhenNotFound = false)
            => new()
            {
                Engine = SqlEngine.Sqlite,
                ConnectionString = ConnectionString,
                Table = "Produit",
                SourceProperty = "product_tmpl_id",
                LookupColumn = "OdooId",
                ResultColumn = "ID",
                TargetProperty = "ProduitId",
                FailWhenNotFound = failWhenNotFound,
            };

        private static Entity Product(long odooId, string name)
            => new() { ["OdooId"] = odooId, ["Nom"] = name };

        [Fact]
        public async Task Identity_IsGivenToCreatedUpdatedAndUnchangedRows()
        {
            var created = await Produits().ActAsync([Product(10, "Chaise"), Product(11, "Table")], Context());

            Assert.All(created, e => Assert.Equal(SyncResult.Created, e.SyncResult));
            Assert.Equal(["2", "3"], created.Select(e => e.Metadata[SqlSynchroniserModule.IdMetadataKey]));
            Assert.Equal([2L, 3L], created.Select(e => e["ProduitId"]));
            Assert.All(created, e => Assert.Equal("Produit", e.Metadata[SqlSynchroniserModule.TableMetadataKey]));

            // Second run: one product renamed, one unchanged. Both still carry the ID of their row.
            var second = await Produits().ActAsync([Product(10, "Chaise"), Product(11, "Table ronde")], Context());

            var unchanged = second.Single(e => (long)e["OdooId"]! == 10);
            var updated = second.Single(e => (long)e["OdooId"]! == 11);
            Assert.Equal(SyncResult.Unchanged, unchanged.SyncResult);
            Assert.Equal(SyncResult.Updated, updated.SyncResult);
            Assert.Equal(2L, unchanged["ProduitId"]);
            Assert.Equal(3L, updated["ProduitId"]);
            Assert.Equal("Table ronde", Rows("SELECT Nom FROM Produit WHERE ID = 3").Single()["Nom"]);
        }

        [Fact]
        public async Task IdentityColumn_IsNeverWrittenNorCompared()
        {
            // The source carries a property named like the generated column: it must not be inserted nor make the row "changed".
            var product = Product(20, "Lampe");
            product["ID"] = 500L;

            var created = Assert.Single(await Produits().ActAsync([product], Context()));
            Assert.Equal(SyncResult.Created, created.SyncResult);
            Assert.Equal(2L, created["ProduitId"]);

            var again = Product(20, "Lampe");
            again["ID"] = 777L;
            Assert.Equal(SyncResult.Unchanged, Assert.Single(await Produits().ActAsync([again], Context())).SyncResult);
            Assert.Empty(Rows("SELECT * FROM Produit WHERE ID IN (500, 777)"));
        }

        [Fact]
        public async Task IdentityColumn_CannotBeAKeyField()
        {
            var context = Context();
            context.Sync.KeyFields = ["ID"];

            var exception = await Assert.ThrowsAsync<OrtolanConfigurationException>(() => Produits().ActAsync([Product(30, "Bureau")], context));
            Assert.Contains("clé", exception.Message);
        }

        [Fact]
        public async Task IdentityProperty_IsOptional_TheMetadataIsAlwaysSet()
        {
            var created = Assert.Single(await Produits(m => m.IdentityProperty = null).ActAsync([Product(40, "Étagère")], Context()));

            Assert.Equal("2", created.Metadata[SqlSynchroniserModule.IdMetadataKey]);
            Assert.False(created.Has("ProduitId"));
        }

        [Fact]
        public async Task Lookup_ResolvesTheForeignKey_AndReportsMissingRows()
        {
            await Produits().ActAsync([Product(10, "Chaise")], Context());

            var articles = new List<Entity>
            {
                new() { ["OdooId"] = 100L, ["Code"] = "CH-01", ["product_tmpl_id"] = "10" },
                new() { ["OdooId"] = 101L, ["Code"] = "CH-02", ["product_tmpl_id"] = 10L },
                new() { ["OdooId"] = 102L, ["Code"] = "XX-01", ["product_tmpl_id"] = 404L },
            };

            var output = await ProduitLookup().ActAsync(articles, Context());

            Assert.Equal(2L, output[0]["ProduitId"]);
            Assert.Equal(2L, output[1]["ProduitId"]);
            Assert.Null(output[2]["ProduitId"]);
            Assert.False(output[2].IsFailed);

            var strict = await ProduitLookup(failWhenNotFound: true).ActAsync([new Entity { ["OdooId"] = 103L, ["product_tmpl_id"] = 404L }], Context());
            Assert.True(Assert.Single(strict).IsFailed);
        }

        [Fact]
        public async Task Lookup_UnknownColumn_IsAConfigurationError()
        {
            var lookup = ProduitLookup();
            lookup.ResultColumn = "Absente";

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => lookup.ActAsync([new Entity { ["product_tmpl_id"] = 1L }], Context()));
        }

        /// <summary>
        /// The scenario of the documentation: products, then articles referencing them by the local ID. The first run fails on
        /// an article; the second run finds the product unchanged, still gets its ID and creates the article.
        /// </summary>
        [Fact]
        public async Task ProduitThenArticle_ReferencesTheLocalId_AndSurvivesARerun()
        {
            List<Entity> OdooProducts() => [Product(10, "Chaise"), Product(11, "Table")];
            List<Entity> OdooArticles(string? secondCode) =>
            [
                new() { ["OdooId"] = 100L, ["Code"] = "CH-01", ["product_tmpl_id"] = 10L },
                new() { ["OdooId"] = 110L, ["Code"] = secondCode, ["product_tmpl_id"] = 11L },
            ];

            // Run 1: the second article has no code (NOT NULL): only this article fails.
            await Produits().ActAsync(OdooProducts(), Context());
            var firstRun = await Articles().ActAsync(await ProduitLookup().ActAsync(OdooArticles(null), Context()), Context());

            Assert.Equal(SyncResult.Created, firstRun[0].SyncResult);
            Assert.Equal(SyncResult.Failed, firstRun[1].SyncResult);

            // Run 2: the products are unchanged, the articles find their product and the missing one is created.
            var products = await Produits().ActAsync(OdooProducts(), Context());
            Assert.All(products, e => Assert.Equal(SyncResult.Unchanged, e.SyncResult));

            var secondRun = await Articles().ActAsync(await ProduitLookup().ActAsync(OdooArticles("TA-01"), Context()), Context());
            Assert.Equal([SyncResult.Created, SyncResult.Unchanged], secondRun.Select(e => e.SyncResult).Order());

            var rows = Rows("SELECT a.Code, p.Nom FROM Article a JOIN Produit p ON p.ID = a.ProduitId ORDER BY a.Code");
            Assert.Equal(["CH-01|Chaise", "TA-01|Table"], rows.Select(r => $"{r["Code"]}|{r["Nom"]}"));
        }
    }
}
