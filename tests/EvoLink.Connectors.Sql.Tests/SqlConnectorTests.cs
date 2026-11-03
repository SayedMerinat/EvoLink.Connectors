using Microsoft.Data.Sqlite;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Sql.Tests
{
    public sealed class SqlConnectorTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"ortolan-sql-{Guid.NewGuid():N}.db");
        private string ConnectionString => $"Data Source={_path};Pooling=False";

        public SqlConnectorTests()
        {
            Execute("""
                CREATE TABLE Clients (
                    Id INTEGER NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Amount REAL NULL,
                    Since TEXT NULL,
                    Active INTEGER NULL,
                    Deleted INTEGER NOT NULL DEFAULT 0,
                    Note TEXT NULL);
                INSERT INTO Clients (Id, Name, Amount, Since, Active) VALUES (9, 'Zoé', 1, '2020-01-01', 1);
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
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private List<Dictionary<string, object?>> Rows(string sql = "SELECT * FROM Clients ORDER BY Id")
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

        private static JobContext Context(SyncMode mode = SyncMode.Full, DateTimeOffset? lastRun = null)
            => new()
            {
                JobId = Guid.NewGuid().ToString("N"),
                ServiceId = "sql-tests",
                Mode = mode,
                Sync = new SyncOptions { KeyFields = ["id"] },
                LastSuccessfulRun = lastRun,
            };

        private SqlSynchroniserModule Synchroniser(Action<SqlSynchroniserModule>? configure = null)
        {
            var module = new SqlSynchroniserModule
            {
                Engine = SqlEngine.Sqlite,
                ConnectionString = ConnectionString,
                Table = "Clients",
                Fields = new Dictionary<string, string> { ["id"] = "Id", ["name"] = "Name", ["amount"] = "Amount", ["since"] = "Since", ["active"] = "Active", ["note"] = "Note" },
            };

            configure?.Invoke(module);
            return module;
        }

        private static List<Entity> Source(params (string Id, string Name, string Amount, string Since, string Active)[] rows)
            => rows.Select(r => new Entity { ["id"] = r.Id, ["name"] = r.Name, ["amount"] = r.Amount, ["since"] = r.Since, ["active"] = r.Active, ["unknown"] = "ignorée" }).ToList();

        [Fact]
        public async Task Synchroniser_CreatesUpdatesAndDeletes_WithTypedComparison()
        {
            var first = await Synchroniser().ActAsync(Source(("1", "Alice", "12,50", "06.06.2017", "oui"), ("2", "Bob", "3", "2018-01-02", "0")), Context(SyncMode.Mirror));

            Assert.Equal([SyncResult.Created, SyncResult.Created, SyncResult.Deleted], first.Select(e => e.SyncResult).Order());
            var rows = Rows();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);

            // Same data written differently: nothing changes, the values are compared once converted to the column types
            // (SQLite only knows INTEGER, REAL and TEXT: the dates are compared as text there, see ColumnTypes_ConvertTheEntityValues).
            var second = await Synchroniser().ActAsync(Source(("1", "Alice", "12.5", "06.06.2017", "1"), ("2", "Bob", "3.00", "2018-01-02", "non")), Context());
            Assert.All(second, e => Assert.Equal(SyncResult.Unchanged, e.SyncResult));

            var third = await Synchroniser().ActAsync(Source(("1", "Alice Martin", "12.5", "2017-06-06", "1")), Context(SyncMode.Mirror));
            Assert.Contains(third, e => e.SyncResult == SyncResult.Updated);
            Assert.Contains(third, e => e.SyncResult == SyncResult.Deleted);
            Assert.Equal("Alice Martin", Assert.Single(Rows())["Name"]);
        }

        [Fact]
        public async Task RefusedRecord_FailsOnlyItsEntity()
        {
            var entities = Source(("1", "Alice", "1", "2020-01-01", "1"), ("2", "", "1", "2020-01-01", "1"));
            entities[1]["name"] = null;

            var output = await Synchroniser().ActAsync(entities, Context());

            Assert.Equal(SyncResult.Created, output.Single(e => e.GetString("id") == "1").SyncResult);
            var failed = output.Single(e => e.GetString("id") == "2");
            Assert.Equal(SyncResult.Failed, failed.SyncResult);
            Assert.Contains("NOT NULL", failed.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SoftDelete_UpdatesTheColumnInsteadOfDeleting()
        {
            var output = await Synchroniser(m => { m.SoftDeleteColumn = "Deleted"; m.Where = "Deleted = 0"; }).ActAsync([], Context(SyncMode.Mirror));

            Assert.Equal(SyncResult.Deleted, Assert.Single(output).SyncResult);
            Assert.Equal(1L, Assert.Single(Rows())["Deleted"]);

            var again = await Synchroniser(m => { m.SoftDeleteColumn = "Deleted"; m.Where = "Deleted = 0"; }).ActAsync([], Context(SyncMode.Mirror));
            Assert.Empty(again);
        }

        [Fact]
        public async Task Transaction_IsCommittedAtTheEnd()
        {
            var output = await Synchroniser(m => m.UseTransaction = true).ActAsync(Source(("1", "Alice", "1", "2020-01-01", "1")), Context());

            Assert.Equal(SyncResult.Created, Assert.Single(output).SyncResult);
            Assert.Equal(2, Rows().Count);
        }

        [Fact]
        public async Task UnreachableDatabase_IsATargetUnavailability()
        {
            var module = Synchroniser(m => m.ConnectionString = $"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.db")};Mode=ReadOnly");

            await Assert.ThrowsAsync<TargetUnavailableException>(() => module.ActAsync(Source(("1", "Alice", "1", "2020-01-01", "1")), Context()));
        }

        [Fact]
        public async Task Provider_ReadsTheTable_WithMappingFilterAndIncrementalColumn()
        {
            Execute("INSERT INTO Clients (Id, Name, Since) VALUES (10, 'Récent', '2026-11-01');");

            var provider = new SqlProviderModule
            {
                Engine = SqlEngine.Sqlite,
                ConnectionString = ConnectionString,
                Table = "Clients",
                Where = "Deleted = 0",
                Fields = new Dictionary<string, string> { ["id"] = "Id", ["nom"] = "Name" },
                OnlyMappedFields = true,
            };

            var all = await provider.FetchAsync(Context());
            Assert.Equal(2, all.Count);
            Assert.Equal(["id", "nom"], all[0].Properties.Keys.Order());
            Assert.Equal(9L, all[0]["id"]);

            provider.IncrementalColumn = "Since";
            var recent = await provider.FetchAsync(Context(lastRun: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            Assert.Equal("Récent", Assert.Single(recent)["nom"]);
        }

        [Fact]
        public void ColumnTypes_ConvertTheEntityValues()
        {
            Assert.Equal(new DateTime(2017, 6, 6), SqlValues.ToColumnType("06.06.2017", typeof(DateTime)));
            Assert.Equal(12.5m, SqlValues.ToColumnType("12,50", typeof(decimal)));
            Assert.Equal(true, SqlValues.ToColumnType("oui", typeof(bool)));
            Assert.Equal(1L, SqlValues.ToColumnType("oui", typeof(int)));
            Assert.Null(SqlValues.ToColumnType("  ", typeof(int)));
            Assert.Equal("pas un nombre", SqlValues.ToColumnType("pas un nombre", typeof(int)));
        }

        [Fact]
        public void Dialect_QuotesIdentifiersAndUsesPositionalParametersForOdbc()
        {
            Assert.Equal("[dbo].[My]]Table]", new SqlDialect(SqlEngine.SqlServer).Quote("dbo.My]Table"));
            Assert.Equal("`clients`", new SqlDialect(SqlEngine.MySql).Quote("clients"));
            Assert.Equal("?", new SqlDialect(SqlEngine.Odbc).Parameter(3));
            Assert.Equal("@p3", new SqlDialect(SqlEngine.SqlServer).Parameter(3));
        }

        [Fact]
        public void Registry_ExposesTheModulesWithTheirConnectionStringSensitive()
        {
            var registry = new TypeRegistry().RegisterAssembly(typeof(SqlSynchroniserModule).Assembly);

            Assert.True(registry.TryGetType("sql-provider", out var provider));
            Assert.True(registry.TryGetType("sql-synchroniser", out var synchroniser));
            Assert.Equal("Synchroniser", registry.GetCategory(synchroniser));
            Assert.Contains(TypeRegistry.GetConfigurableProperties(provider), p => p.Name == "ConnectionString" && TypeRegistry.IsSensitive(p));
        }
    }
}
