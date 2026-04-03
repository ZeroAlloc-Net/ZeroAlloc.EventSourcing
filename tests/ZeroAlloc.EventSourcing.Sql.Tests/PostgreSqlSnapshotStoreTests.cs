using FluentAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Contract tests for <see cref="PostgreSqlSnapshotStore{TState}"/>.
/// Inherits all contract tests from <see cref="SnapshotStoreContractTests{TStore}"/>.
/// </summary>
public class PostgreSqlSnapshotStoreTests : SnapshotStoreContractTests<PostgreSqlSnapshotStore<SnapshotTestState>>
{
    /// <summary>Test that constructor rejects null dataSource.</summary>
    [Fact]
    public void Constructor_NullDataSource_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new PostgreSqlSnapshotStore<SnapshotTestState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    /// <summary>Test that constructor accepts valid dataSource.</summary>
    [Fact]
    public void Constructor_ValidDataSource_Succeeds()
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test;Username=postgres;Password=postgres");

        FluentActions.Invoking(() => new PostgreSqlSnapshotStore<SnapshotTestState>(dataSource))
            .Should().NotThrow();
    }

    /// <inheritdoc/>
    protected override async Task<PostgreSqlSnapshotStore<SnapshotTestState>> CreateStoreAsync()
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test;Username=postgres;Password=postgres");
        var store = new PostgreSqlSnapshotStore<SnapshotTestState>(dataSource);
        // EnsureSchemaAsync is called to initialize the database schema
        // In real tests with a database, this would create the table
        // For unit tests with dummy connection strings, this is a no-op
        try
        {
            await store.EnsureSchemaAsync();
        }
        catch
        {
            // Ignore connection errors for unit tests with dummy connection strings
        }
        return store;
    }

    /// <inheritdoc/>
    public override async Task DisposeAsync()
    {
        // No resources to dispose for unit tests with dummy connection strings
        await Task.CompletedTask;
    }
}
