using Ortolan.Core.Models;

namespace EvoLink.Connectors.Sql
{
    /// <summary>
    /// Columns of a table created (or completed) by the synchroniser from the entities it receives: one column per property, in the
    /// order the properties first appear, with the type given in the configuration or deduced from the values.
    /// </summary>
    internal static class SqlSchema
    {
        /// <summary>Engines whose DDL is known (ODBC and OleDB can point to anything).</summary>
        public static bool Supports(SqlEngine engine)
            => engine is SqlEngine.SqlServer or SqlEngine.Sqlite or SqlEngine.MySql;

        /// <summary>Columns wanted for the entities: (column, SQL type), the explicit types first applied.</summary>
        public static List<(string Column, string Type)> Plan(SqlEngine engine, IEnumerable<Entity> entities, Func<string, string?> columnOf,
            IReadOnlyDictionary<string, string> explicitTypes, string? identityColumn)
        {
            var columns = new List<string>();
            var samples = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in entities)
            {
                foreach (var (property, value) in entity.Properties)
                {
                    if (columnOf(property) is not { } column || string.Equals(column, identityColumn, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!samples.ContainsKey(column))
                    {
                        columns.Add(column);
                        samples[column] = null;
                    }

                    if (samples[column] == null && value != null)
                        samples[column] = value;
                }
            }

            foreach (var column in explicitTypes.Keys)
            {
                if (!samples.ContainsKey(column) && !string.Equals(column, identityColumn, StringComparison.OrdinalIgnoreCase))
                {
                    columns.Add(column);
                    samples[column] = null;
                }
            }

            return columns.Select(c => (c, TypeOf(engine, c, samples[c], explicitTypes))).ToList();
        }

        public static string CreateTable(SqlDialect dialect, string table, IReadOnlyList<(string Column, string Type)> columns, string? identityColumn)
        {
            var definitions = new List<string>();

            if (!string.IsNullOrWhiteSpace(identityColumn))
                definitions.Add($"{dialect.Quote(identityColumn)} {IdentityType(dialect.Engine)}");

            definitions.AddRange(columns.Select(c => $"{dialect.Quote(c.Column)} {c.Type} NULL"));

            return $"CREATE TABLE {dialect.Quote(table)} ({string.Join(", ", definitions)})";
        }

        public static string AddColumn(SqlDialect dialect, string table, string column, string type)
            => dialect.Engine == SqlEngine.SqlServer
                ? $"ALTER TABLE {dialect.Quote(table)} ADD {dialect.Quote(column)} {type} NULL"
                : $"ALTER TABLE {dialect.Quote(table)} ADD COLUMN {dialect.Quote(column)} {type} NULL";

        /// <summary>The type comes from the configuration: it is checked so that it can only be a type.</summary>
        public static string? CheckType(string type)
            => type.Contains(';') || type.Contains("--") || type.Contains("/*") ? $"Le type SQL '{type}' n'est pas valide." : null;

        private static string TypeOf(SqlEngine engine, string column, object? sample, IReadOnlyDictionary<string, string> explicitTypes)
        {
            if (explicitTypes.TryGetValue(column, out var type) && !string.IsNullOrWhiteSpace(type))
                return type.Trim();

            return (engine, sample) switch
            {
                (SqlEngine.SqlServer, bool) => "BIT",
                (SqlEngine.SqlServer, long or int or short or byte) => "BIGINT",
                (SqlEngine.SqlServer, decimal) => "DECIMAL(38, 10)",
                (SqlEngine.SqlServer, double or float) => "FLOAT",
                (SqlEngine.SqlServer, DateTime) => "DATETIME2",
                (SqlEngine.SqlServer, DateTimeOffset) => "DATETIMEOFFSET",
                (SqlEngine.SqlServer, _) => "NVARCHAR(MAX)",

                (SqlEngine.MySql, bool) => "TINYINT(1)",
                (SqlEngine.MySql, long or int or short or byte) => "BIGINT",
                (SqlEngine.MySql, decimal) => "DECIMAL(38, 10)",
                (SqlEngine.MySql, double or float) => "DOUBLE",
                (SqlEngine.MySql, DateTime or DateTimeOffset) => "DATETIME(6)",
                (SqlEngine.MySql, _) => "LONGTEXT",

                (_, bool or long or int or short or byte) => "INTEGER",
                (_, decimal) => "NUMERIC",
                (_, double or float) => "REAL",
                _ => "TEXT",
            };
        }

        private static string IdentityType(SqlEngine engine)
            => engine switch
            {
                SqlEngine.SqlServer => "BIGINT IDENTITY(1, 1) NOT NULL PRIMARY KEY",
                SqlEngine.MySql => "BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY",
                _ => "INTEGER PRIMARY KEY AUTOINCREMENT",
            };
    }
}
