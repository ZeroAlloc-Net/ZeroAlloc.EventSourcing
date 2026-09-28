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
        // DROP DATABASE fails while a connection is open on the database, and the test leaves its
        // connections in the client pool. Closing that pool frees them; SET SINGLE_USER WITH
        // ROLLBACK IMMEDIATE would too, but took three seconds per test.
        using (var pooled = new SqlConnection(connectionString))
            SqlConnection.ClearPool(pooled);

        await ExecuteOnServerAsync($"DROP DATABASE [{name}]");
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
