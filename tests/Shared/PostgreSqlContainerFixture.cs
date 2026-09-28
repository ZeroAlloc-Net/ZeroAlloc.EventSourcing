using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ZeroAlloc.EventSourcing.Testing;

/// <summary>
/// Starts one PostgreSQL container for a whole test collection. Tests used to start a container
/// each, which put a single test project at ten minutes. Each test still gets an empty database
/// of its own from <see cref="CreateDatabaseAsync"/>, so no test sees another test's tables.
/// </summary>
public sealed class PostgreSqlContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc/>
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc/>
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database; disposing the result drops it again.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{name}\"");

        var connectionString =
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;

        // FORCE ends the connections the test left open, which DROP DATABASE would otherwise refuse.
        return new TestDatabase(
            connectionString, () => ExecuteOnServerAsync($"DROP DATABASE \"{name}\" WITH (FORCE)"));
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>The tests that share one <see cref="PostgreSqlContainerFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlContainerFixture>
{
    /// <summary>The collection name to put on a test class.</summary>
    public const string Name = "PostgreSQL";
}
