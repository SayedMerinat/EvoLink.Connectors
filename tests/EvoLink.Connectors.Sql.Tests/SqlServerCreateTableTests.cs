using Microsoft.Data.SqlClient;
using Ortolan.Core.Models;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.Sql.Tests
{
    /// <summary>
    /// createTable / addMissingColumns on SQL Server (LocalDB of the development machine), the engine of the OdooToolbox exports:
    /// types of the columns, Unicode, identity. Skipped when LocalDB is not installed.
    /// </summary>
    public sealed class SqlServerCreateTableTests : IDisposable
    {
        private const string Server = @"(localdb)\MSSQLLocalDB";
        private readonly string _database = "ConnectorsTests_" + Guid.NewGuid().ToString("N")[..8];
        private readonly bool _available;

        public SqlServerCreateTableTests()
        {
            try
            {
                Execute("master", $"CREATE DATABASE [{_database}]");
                _available = true;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                _available = false;
            }
        }

        public void Dispose()
        {
            if (!_available)
                return;

            SqlConnection.ClearAllPools();
            Execute("master", $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]");
        }

        private string ConnectionString(string database)
            => new SqlConnectionStringBuilder { DataSource = Server, InitialCatalog = database, IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 15 }.ToString();

        private object? Execute(string database, string sql)
        {
            using var connection = new SqlConnection(ConnectionString(database));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        private List<string> Columns(string table)
        {
            using var connection = new SqlConnection(ConnectionString(_database));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT c.name + ' ' + UPPER(t.name) + CASE WHEN t.name IN ('nvarchar', 'varchar') THEN '(' + CASE c.max_length WHEN -1 THEN 'MAX' ELSE CAST(c.max_length AS varchar) END + ')' ELSE '' END
                FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID('{table}') ORDER BY c.column_id
                """;
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(0));

            return columns;
        }

        private static JobContext Context()
            => new()
            {
                JobId = Guid.NewGuid().ToString("N"),
                ServiceId = "sqlserver-create-tests",
                Mode = SyncMode.Full,
                Sync = new SyncOptions { KeyFields = ["id"], DeleteMissing = true },
            };

        private SqlSynchroniserModule Synchroniser(Action<SqlSynchroniserModule>? configure = null)
        {
            var module = new SqlSynchroniserModule { Engine = SqlEngine.SqlServer, ConnectionString = ConnectionString(_database), Table = "dbo.my_products" };
            configure?.Invoke(module);
            return module;
        }

        [SkippableFact]
        public async Task CreateTable_OnSqlServer_LikeOdootosql()
        {
            Skip.IfNot(_available, "SQL Server LocalDB indisponible sur cette machine.");

            var products = new List<Entity>
            {
                new() { ["id"] = 1L, ["active"] = true, ["name"] = "😀 Привет € ’ é", ["list_price"] = 1234.5678901m, ["qty"] = 0L, ["date_start"] = new DateTime(2024, 3, 1), ["categ_id"] = "[5, 'Mobilier']" },
                new() { ["id"] = 2L, ["active"] = false, ["name"] = "L'été", ["list_price"] = null, ["qty"] = 7L, ["date_start"] = null, ["categ_id"] = null },
            };

            var output = await Synchroniser(s =>
            {
                s.CreateTable = true;
                s.ColumnTypes = new() { ["date_start"] = "DATE", ["list_price"] = "FLOAT(53)" };
            }).ActAsync(products, Context());

            Assert.All(output, e => Assert.Equal(SyncResult.Created, e.SyncResult));
            Assert.Equal(["id BIGINT", "active BIT", "name NVARCHAR(MAX)", "list_price FLOAT", "qty BIGINT", "date_start DATE", "categ_id NVARCHAR(MAX)"], Columns("dbo.my_products"));

            // Unicode kept (parameters), 0 kept, full precision.
            Assert.Equal("😀 Привет € ’ é", Execute(_database, "SELECT name FROM dbo.my_products WHERE id = 1"));
            Assert.Equal(0L, Execute(_database, "SELECT qty FROM dbo.my_products WHERE id = 1"));
            Assert.Equal(1234.5678901d, (double)Execute(_database, "SELECT list_price FROM dbo.my_products WHERE id = 1")!, 9);

            // A field added afterwards becomes a column; the existing rows are kept.
            products[0]["default_code"] = "A001";
            await Synchroniser(s => s.AddMissingColumns = true).ActAsync(products, Context());

            Assert.Equal("default_code NVARCHAR(MAX)", Columns("dbo.my_products").Last());
            Assert.Equal("A001", Execute(_database, "SELECT default_code FROM dbo.my_products WHERE id = 1"));
            Assert.Equal(2, Execute(_database, "SELECT COUNT(*) FROM dbo.my_products"));
        }

        [SkippableFact]
        public async Task CreateTable_WithIdentity_OnSqlServer()
        {
            Skip.IfNot(_available, "SQL Server LocalDB indisponible sur cette machine.");

            var output = await Synchroniser(s =>
            {
                s.CreateTable = true;
                s.Table = "dbo.Produit";
                s.IdentityColumn = "ID";
                s.KeyFields = ["OdooId"];
            }).ActAsync([new() { ["OdooId"] = 10L, ["Nom"] = "Chaise" }, new() { ["OdooId"] = 11L, ["Nom"] = "Table" }], Context());

            Assert.Equal(["ID BIGINT", "OdooId BIGINT", "Nom NVARCHAR(MAX)"], Columns("dbo.Produit"));
            Assert.Equal(["1", "2"], output.Select(e => e.Metadata[SqlSynchroniserModule.IdMetadataKey]));
        }
    }
}
