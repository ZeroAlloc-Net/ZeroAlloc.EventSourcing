using Npgsql;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Factory implementation for creating database-specific snapshot store instances at runtime.
/// Supports PostgreSQL and SQL Server based on configuration.
/// </summary>
public sealed class SnapshotStoreProvider : ISnapshotStoreProvider
{
    private readonly string _database;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the SnapshotStoreProvider.
    /// </summary>
    /// <param name="database">The database type: "PostgreSQL" or "SqlServer".</param>
    /// <param name="connectionString">The connection string for the database.</param>
    /// <exception cref="ArgumentNullException">If database or connectionString is null.</exception>
    /// <exception cref="ArgumentException">If database is not "PostgreSQL" or "SqlServer".</exception>
    public SnapshotStoreProvider(string database, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(connectionString);

        if (!database.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) &&
            !database.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Database must be 'PostgreSQL' or 'SqlServer', got '{database}'",
                nameof(database));
        }

        _database = database;
        _connectionString = connectionString;
    }

    /// <summary>
    /// Creates a snapshot store instance for the configured database.
    /// </summary>
    /// <typeparam name="TState">The aggregate state type.</typeparam>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A PostgreSqlSnapshotStore or SqlServerSnapshotStore instance.</returns>
    /// <exception cref="OperationCanceledException">If cancellation is requested.</exception>
    public async ValueTask<ISnapshotStore<TState>> CreateAsync<TState>(CancellationToken ct = default)
        where TState : struct, IAggregateState<TState>
    {
        ct.ThrowIfCancellationRequested();

        if (_database.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            var dataSource = NpgsqlDataSource.Create(_connectionString);
            return new PostgreSqlSnapshotStore<TState>(dataSource);
        }
        else
        {
            return new SqlServerSnapshotStore<TState>(_connectionString);
        }
    }
}
