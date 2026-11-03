using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.OleDb;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;

namespace EvoLink.Connectors.Sql
{
    public enum SqlEngine
    {
        SqlServer,
        MySql,
        Odbc,
        OleDb,
        Sqlite,
    }

    /// <summary>
    /// SQL text specific to an engine: identifier quoting and parameter placeholders.
    /// </summary>
    public sealed class SqlDialect
    {
        public SqlEngine Engine { get; }

        public SqlDialect(SqlEngine engine)
        {
            Engine = engine;
        }

        /// <summary>ODBC and OleDB only accept positional "?" parameters.</summary>
        public bool PositionalParameters => Engine is SqlEngine.Odbc or SqlEngine.OleDb;

        public string Parameter(int index)
            => PositionalParameters ? "?" : $"@p{index}";

        public string ParameterName(int index)
            => PositionalParameters ? $"p{index}" : $"@p{index}";

        /// <summary>Quotes an identifier ("schema.table" is quoted part by part).</summary>
        public string Quote(string identifier)
            => string.Join(".", identifier.Split('.').Select(QuotePart));

        private string QuotePart(string part)
            => Engine switch
            {
                SqlEngine.SqlServer or SqlEngine.OleDb => $"[{part.Replace("]", "]]")}]",
                SqlEngine.MySql => $"`{part.Replace("`", "``")}`",
                _ => $"\"{part.Replace("\"", "\"\"")}\"",
            };
    }

    /// <summary>
    /// Access to a SQL database, abstracted for the tests.
    /// </summary>
    public interface ISqlGateway : IAsyncDisposable
    {
        SqlDialect Dialect { get; }
        Task OpenAsync(CancellationToken cancellationToken);
        Task BeginTransactionAsync(CancellationToken cancellationToken);
        Task CommitAsync(CancellationToken cancellationToken);
        Task<List<Dictionary<string, object?>>> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken);

        /// <summary>CLR type of each column of a query, from its schema (no row read).</summary>
        Task<Dictionary<string, Type>> GetColumnTypesAsync(string sql, CancellationToken cancellationToken);

        Task<int> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken);

        /// <summary>True when the last failure broke the connection (the database is unreachable rather than the record refused).</summary>
        bool IsConnectionBroken { get; }
    }

    /// <summary>
    /// ADO.NET implementation. A connection is not thread-safe: the commands are serialised.
    /// </summary>
    public sealed class DbSqlGateway : ISqlGateway
    {
        private readonly DbProviderFactory _factory;
        private readonly string _connectionString;
        private readonly int _commandTimeout;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private DbConnection? _connection;
        private DbTransaction? _transaction;

        public SqlDialect Dialect { get; }

        public bool IsConnectionBroken => _connection is not { State: ConnectionState.Open };

        public DbSqlGateway(SqlEngine engine, string connectionString, int commandTimeoutSeconds)
        {
            Dialect = new SqlDialect(engine);
            _connectionString = connectionString;
            _commandTimeout = commandTimeoutSeconds;
            _factory = engine switch
            {
                SqlEngine.SqlServer => SqlClientFactory.Instance,
                SqlEngine.MySql => MySqlClientFactory.Instance,
                SqlEngine.Odbc => OdbcFactory.Instance,
                SqlEngine.OleDb => OperatingSystem.IsWindows() ? OleDbFactory.Instance : throw new PlatformNotSupportedException("OleDB n'est disponible que sous Windows."),
                _ => SqliteFactory.Instance,
            };
        }

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (_connection != null)
                return;

            var connection = _factory.CreateConnection() ?? throw new InvalidOperationException("Le fournisseur SQL n'a pas créé de connexion.");
            connection.ConnectionString = _connectionString;
            await connection.OpenAsync(cancellationToken);
            _connection = connection;
        }

        public async Task BeginTransactionAsync(CancellationToken cancellationToken)
        {
            await OpenAsync(cancellationToken);
            _transaction ??= await _connection!.BeginTransactionAsync(cancellationToken);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_transaction == null)
                return;

            await _transaction.CommitAsync(cancellationToken);
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        public Task<List<Dictionary<string, object?>>> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
            => RunAsync(async command =>
            {
                var rows = new List<Dictionary<string, object?>>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                    rows.Add(row);
                }

                return rows;
            }, sql, parameters, cancellationToken);

        public Task<Dictionary<string, Type>> GetColumnTypesAsync(string sql, CancellationToken cancellationToken)
            => RunAsync(async command =>
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);
                var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < reader.FieldCount; i++)
                    types[reader.GetName(i)] = reader.GetFieldType(i);

                return types;
            }, sql, [], cancellationToken);

        public Task<int> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
            => RunAsync(command => command.ExecuteNonQueryAsync(cancellationToken), sql, parameters, cancellationToken);

        private async Task<T> RunAsync<T>(Func<DbCommand, Task<T>> action, string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
        {
            await OpenAsync(cancellationToken);
            await _lock.WaitAsync(cancellationToken);

            try
            {
                await using var command = _connection!.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = _commandTimeout;
                command.Transaction = _transaction;

                for (var i = 0; i < parameters.Count; i++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = Dialect.ParameterName(i);
                    parameter.Value = parameters[i] ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                return await action(command);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction != null)
            {
                try
                {
                    await _transaction.RollbackAsync();
                }
                catch (DbException)
                {
                    // The transaction may already be rolled back by the server.
                }

                await _transaction.DisposeAsync();
                _transaction = null;
            }

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            _lock.Dispose();
        }
    }
}
