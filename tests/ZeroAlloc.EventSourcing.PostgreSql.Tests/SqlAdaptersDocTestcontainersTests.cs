// The "PostgreSQL with Testcontainers" example from docs/usage-guides/sql-adapters.md, copied as it
// appears there and run: it starts its own container, as the docs show. When the snippet changes in
// the docs, change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.PostgreSql.Tests.Docs;

// --- snippet: "PostgreSQL with Testcontainers" ---
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

public class PostgreSqlEventStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task AppendAsync_SavesEvents()
    {
        // Arrange: the adapter stores serialized events; EventStore creates these for you
        var streamId = new StreamId("test-stream");
        var raw = new RawEvent(StreamPosition.Start, "OrderPlaced", Encoding.UTF8.GetBytes("{}"), EventMetadata.New("OrderPlaced"));

        // Act
        var appended = await _adapter.AppendAsync(streamId, new[] { raw }, StreamPosition.Start);

        // Assert
        Assert.True(appended.IsSuccess);

        var count = 0;
        await foreach (var _ in _adapter.ReadAsync(streamId, StreamPosition.Start))
        {
            count++;
        }

        Assert.Equal(1, count);
    }
}
// --- end snippet ---
