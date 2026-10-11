using Npgsql;
using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

/// <summary><see cref="PostgreSqlEventStoreAdapter"/> against the bounded read contract: the query carries a LIMIT, see #473.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class BoundedReadTests(PostgreSqlContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _dataSource = NpgsqlDataSource.Create(_database.GetConnectionString());
        _adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public Task Stream_reads_return_at_most_maxCount_events()
        => BoundedReadContract.Stream_reads_return_at_most_maxCount_events_page_by_page(_adapter);

    [Fact]
    public Task Global_reads_return_at_most_maxCount_events()
        => BoundedReadContract.Global_reads_return_at_most_maxCount_events_page_by_page(_adapter);

    [Fact]
    public void Rejects_a_maxCount_below_one()
        => BoundedReadContract.Rejects_a_maxCount_below_one(_adapter);
}
