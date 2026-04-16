using FluentAssertions;
using Testcontainers.MsSql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Integration tests for <see cref="SqlServerCheckpointStore"/> using a real SQL Server instance via Testcontainers.
/// Inherits all contract tests from <see cref="CheckpointStoreContractTests"/>.
/// </summary>
[Collection("SqlServer")]
public sealed class SqlServerCheckpointStoreTests : CheckpointStoreContractTests, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerCheckpointStore _store = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _store = new SqlServerCheckpointStore(_container.GetConnectionString());
        await _store.EnsureSchemaAsync();
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _container.StopAsync();
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
