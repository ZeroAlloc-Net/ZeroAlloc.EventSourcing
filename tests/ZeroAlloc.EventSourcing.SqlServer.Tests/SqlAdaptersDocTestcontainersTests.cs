// The "SQL Server with Testcontainers" example from docs/usage-guides/sql-adapters.md, copied as it
// appears there and run: it starts its own container, as the docs show. When the snippet changes in
// the docs, change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.SqlServer.Tests.Docs;

// --- snippet: "SQL Server with Testcontainers" ---
using System.Text;
using Testcontainers.MsSql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

public class SqlServerEventStoreTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _adapter = new SqlServerEventStoreAdapter(_container.GetConnectionString());
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task AppendAsync_SavesEvents()
    {
        var streamId = new StreamId("test-stream");
        var raw = new RawEvent(StreamPosition.Start, "OrderPlaced", Encoding.UTF8.GetBytes("{}"), EventMetadata.New("OrderPlaced"));

        var appended = await _adapter.AppendAsync(streamId, new[] { raw }, StreamPosition.Start);

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
