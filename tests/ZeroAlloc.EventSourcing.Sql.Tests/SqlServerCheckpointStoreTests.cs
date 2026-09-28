using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Testing;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Integration tests for <see cref="SqlServerCheckpointStore"/> using a real SQL Server instance via Testcontainers.
/// Inherits all contract tests from <see cref="CheckpointStoreContractTests"/>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerCheckpointStoreTests(SqlServerContainerFixture fixture) : CheckpointStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerCheckpointStore _store = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _store = new SqlServerCheckpointStore(_database.GetConnectionString());
        await _store.EnsureSchemaAsync();
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    /// <inheritdoc/>
    protected override ICheckpointStore CreateStore() => _store;

    /// <summary>
    /// Verifies that calling <see cref="SqlServerCheckpointStore.EnsureSchemaAsync"/> multiple times does not throw.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _store.EnsureSchemaAsync();
            await _store.EnsureSchemaAsync();
        });

        exception.Should().BeNull();
    }
}
