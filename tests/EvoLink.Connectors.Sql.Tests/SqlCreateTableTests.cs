using Microsoft.Data.Sqlite;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.Sql.Tests
{
    /// <summary>
    /// createTable / addMissingColumns: the table of an export (odootosql) is created from the entities, then completed when a
    /// field is added, never dropped. Without these options the synchroniser behaves as before.
    /// </summary>
    public sealed class SqlCreateTableTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"ortolan-sql-create-{Guid.NewGuid():N}.db");
        private string ConnectionString => $"Data Source={_path};Pooling=False";

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_path);
        }

        private List<(string Name, string Type)> Columns(string table)
            => Rows($"PRAGMA table_info(\"{table}\")").Select(r => ((string)r["name"]!, (string)r["type"]!)).ToList();

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
                ServiceId = "sql-create-tests",
                Mode = SyncMode.Full,
                Sync = new SyncOptions { KeyFields = ["id"], DeleteMissing = true },
            };

        private SqlSynchroniserModule Synchroniser(Action<SqlSynchroniserModule>? configure = null)
        {
            var module = new SqlSynchroniserModule { Engine = SqlEngine.Sqlite, ConnectionString = ConnectionString, Table = "my_products" };
            configure?.Invoke(module);
            return module;
        }

        private static List<Entity> Products(bool withEmail = false)
            =>
            [
                new() { ["id"] = 1L, ["name"] = "Chaise", ["list_price"] = 12.5m, ["sale_ok"] = true, ["categ_id"] = null, ["date_start"] = new DateTime(2024, 3, 1) },
                new() { ["id"] = 2L, ["name"] = "L'été 😀", ["list_price"] = 0m, ["sale_ok"] = false, ["categ_id"] = "[5, 'Mobilier']", ["date_start"] = null },
                .. withEmail ? [new Entity { ["id"] = 3L, ["name"] = "Lampe", ["email"] = "x@y.ch" }] : Array.Empty<Entity>(),
            ];

        [Fact]
        public async Task CreateTable_CreatesTheColumnsInTheOrderOfTheProperties_WithTheirTypes()
        {
            var output = await Synchroniser(s =>
            {
                s.CreateTable = true;
                s.ColumnTypes = new() { ["date_start"] = "DATE" };
            }).ActAsync(Products(), Context());

            Assert.All(output, e => Assert.Equal(SyncResult.Created, e.SyncResult));
            Assert.Equal(
                [("id", "INTEGER"), ("name", "TEXT"), ("list_price", "NUMERIC"), ("sale_ok", "INTEGER"), ("categ_id", "TEXT"), ("date_start", "DATE")],
                Columns("my_products"));

            var rows = Rows("SELECT * FROM my_products ORDER BY id");
            Assert.Equal("L'été 😀", rows[1]["name"]);
            Assert.Equal(0L, rows[1]["list_price"]);
            Assert.Equal("[5, 'Mobilier']", rows[1]["categ_id"]);

            // Second run: the table exists, nothing is recreated and nothing changes.
            var second = await Synchroniser(s => s.CreateTable = true).ActAsync(Products(), Context());
            Assert.All(second, e => Assert.Equal(SyncResult.Unchanged, e.SyncResult));
        }

        [Fact]
        public async Task AddMissingColumns_AddsTheNewFields_AndKeepsTheRows()
        {
            await Synchroniser(s => s.CreateTable = true).ActAsync(Products(), Context());

            var output = await Synchroniser(s => s.AddMissingColumns = true).ActAsync(Products(withEmail: true), Context());

            Assert.Equal("email", Columns("my_products").Last().Name);
            Assert.Equal(SyncResult.Created, output.Single(e => (long)e["id"]! == 3).SyncResult);
            Assert.Equal("x@y.ch", Rows("SELECT email FROM my_products WHERE id = 3")[0]["email"]);
            Assert.Equal(3, Rows("SELECT * FROM my_products").Count);
        }

        [Fact]
        public async Task WithoutTheOptions_NothingChangesInTheSchema()
        {
            await Synchroniser(s => s.CreateTable = true).ActAsync(Products(), Context());

            // An unknown property is still ignored, the table keeps its columns.
            await Synchroniser().ActAsync(Products(withEmail: true), Context());
            Assert.DoesNotContain(Columns("my_products"), c => c.Name == "email");

            // A missing table is still an unavailable target.
            await Assert.ThrowsAsync<TargetUnavailableException>(() => Synchroniser(s => s.Table = "absente").ActAsync(Products(), Context()));
        }

        [Fact]
        public async Task CreateTable_WithoutAnyEntity_DoesNothing_AndCreatesTheTableLater()
        {
            var empty = await Synchroniser(s => s.CreateTable = true).ActAsync([], Context());

            Assert.Empty(empty);
            Assert.Empty(Columns("my_products"));

            await Synchroniser(s => s.CreateTable = true).ActAsync(Products(), Context());
            Assert.Equal(2, Rows("SELECT * FROM my_products").Count);
        }

        [Theory]
        [InlineData(DateTimeKind.Utc)]
        [InlineData(DateTimeKind.Local)]
        public async Task DatesWithATimeZoneKind_AreUnchangedAtTheNextRun(DateTimeKind kind)
        {
            // odoo-provider gives UTC (timeZone "UTC") or local datetimes; the column stores them without their kind.
            List<Entity> Rows() => [new() { ["id"] = 1L, ["write_date"] = new DateTime(2024, 9, 11, 6, 37, 0, kind) }];

            await Synchroniser(s => s.CreateTable = true).ActAsync(Rows(), Context());
            var second = await Synchroniser().ActAsync(Rows(), Context());

            Assert.Equal(SyncResult.Unchanged, second[0].SyncResult);
        }

        [Fact]
        public async Task CreateTable_WithAnIdentityColumn()
        {
            var output = await Synchroniser(s =>
            {
                s.CreateTable = true;
                s.IdentityColumn = "RowId";
                s.Table = "Produit";
            }).ActAsync(Products(), Context());

            Assert.Equal("RowId", Columns("Produit")[0].Name);
            Assert.Equal(["1", "2"], output.Select(e => e.Metadata[SqlSynchroniserModule.IdMetadataKey]));
        }

        [Theory]
        [InlineData(SqlEngine.Odbc, "TEXT")]
        [InlineData(SqlEngine.Sqlite, "TEXT; DROP TABLE x")]
        public async Task InvalidConfiguration_IsRefused(SqlEngine engine, string type)
        {
            var module = Synchroniser(s =>
            {
                s.CreateTable = true;
                s.ColumnTypes = new() { ["name"] = type };
            });

            if (engine == SqlEngine.Odbc)
            {
                // The ODBC connection itself is not opened in this test: only the check of the option matters.
                module = new TestSqlSynchroniser(module) { Engine = SqlEngine.Odbc };
            }

            await Assert.ThrowsAsync<OrtolanConfigurationException>(() => module.ActAsync(Products(), Context()));
        }

        /// <summary>Same module, with a SQLite gateway whatever the engine configured.</summary>
        private sealed class TestSqlSynchroniser : SqlSynchroniserModule
        {
            public TestSqlSynchroniser(SqlSynchroniserModule source)
            {
                ConnectionString = source.ConnectionString;
                Table = source.Table;
                CreateTable = source.CreateTable;
                ColumnTypes = source.ColumnTypes;
            }

            protected override ISqlGateway CreateGateway()
                => new DbSqlGateway(SqlEngine.Sqlite, ConnectionString, CommandTimeoutSeconds);
        }
    }
}
