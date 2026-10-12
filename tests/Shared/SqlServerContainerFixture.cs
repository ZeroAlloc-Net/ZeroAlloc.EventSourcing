using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace ZeroAlloc.EventSourcing.Testing;

/// <summary>
/// Starts one SQL Server container for a whole test collection. Tests used to start a container
/// each, which put a single test project at ten minutes. Each test still gets an empty database
/// of its own from <see cref="CreateDatabaseAsync"/>, so no test sees another test's tables.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <inheritdoc/>
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc/>
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database; disposing the result drops it again.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE [{name}]");

        var connectionString =
            new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;

        return new TestDatabase(connectionString, () => DropDatabaseAsync(name, connectionString));
    }

    private async Task DropDatabaseAsync(string name, string connectionString)
    {
        // DROP DATABASE fails with error 3702 while a session is open on the database, and the test
        // leaves its connections in the client pool. ClearPool closes them, but only on the client:
        // the server ends each session after it sees the socket close, so a DROP sent straight
        // afterwards can still find one. That happened most after subscription tests, see #478.
        // SET SINGLE_USER WITH ROLLBACK IMMEDIATE ends any session still on the database and waits
        // until it is gone, so the DROP that follows cannot race it. It costs about 250 ms even
        // when there is nothing to end, so it runs only when a session is still on the database or
        // still holds a lock on it. The test is over and its pool is cleared, so no new session can
        // arrive between that check and the DROP.
        using (var pooled = new SqlConnection(connectionString))
            SqlConnection.ClearPool(pooled);

        await ExecuteOnServerAsync(
            $"""
            IF EXISTS (SELECT 1 FROM sys.dm_tran_locks
                       WHERE resource_type = 'DATABASE'
                         AND resource_database_id = DB_ID(N'{name}')
                         AND request_session_id <> @@SPID)
               OR EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'{name}'))
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{name}];
            """);
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>The tests that share one <see cref="SqlServerContainerFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>
{
    /// <summary>The collection name to put on a test class.</summary>
    public const string Name = "SqlServer";
}
