using System.Text;
using AwesomeAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlSubscriptionTests(PostgreSqlContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private PostgreSqlEventStoreAdapter _adapter = null!;
    private NpgsqlDataSource _dataSource = null!;

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

    private static RawEvent MakeRaw(string eventType = "TestEvent")
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Subscribe_CatchesUpHistoricalEvents_BeforeStartAsync()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Subscribe_ReceivesLiveEvents_AppendedAfterStart()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public async Task Subscribe_FromPosition_SkipsEarlierEvents()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
            MakeRaw("OrderDelivered"),
            MakeRaw("OrderRefunded"),
        }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        // EXCLUSIVE semantics: subscribe from position 3 — only events with position > 3 should
        // arrive, i.e. "OrderRefunded" (at position 4).
        var sub = await _adapter.SubscribeAsync(id, new StreamPosition(3), (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sub.DisposeAsync();

        received.Should().HaveCount(1);
        received[0].EventType.Should().Be("OrderRefunded");
    }

    [Fact]
    public async Task Subscribe_AfterDispose_StopsReceiving()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var received = new List<RawEvent>();

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start,
            (e, _) => { received.Add(e); return ValueTask.CompletedTask; });
        await sub.StartAsync();
        await sub.DisposeAsync();

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        // Wait > 2 poll cycles (500 ms each) to confirm no events arrive after dispose.
        await Task.Delay(1100);

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscription_IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var sub = await _adapter.SubscribeAsync(id, StreamPosition.Start, (_, _) => ValueTask.CompletedTask);

        sub.IsRunning.Should().BeFalse();
        await sub.StartAsync();
        sub.IsRunning.Should().BeTrue();
        await sub.DisposeAsync();
        sub.IsRunning.Should().BeFalse();
    }

    // The in-memory adapter keeps the position it is given, as EventStore sets it; SQL adapters assign their own.
    private static RawEvent Raw(string eventType, long position)
        => MakeRaw(eventType) with { Position = new StreamPosition(position) };

    // Issue 409: after delivering the event at position N the subscription read from N + 1,
    // and reads exclude their start position, so the first event of every later poll cycle
    // was lost. Each append below lands in its own poll cycle.
    [Fact]
    public async Task Subscribe_DeliversEveryLiveEvent_AcrossPollCycles()
    {
        var adapter = _adapter;
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await adapter.AppendAsync(id, new[] { Raw("E1", 1) }.AsMemory(), StreamPosition.Start);

        var received = new List<string>();
        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            lock (received) received.Add(e.EventType);
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();
        try
        {
            await PollingTestHelpers.WaitForCountAsync(received, 1);
            await adapter.AppendAsync(id, new[] { Raw("E2", 2) }.AsMemory(), new StreamPosition(1));
            await PollingTestHelpers.WaitForCountAsync(received, 2);
            await adapter.AppendAsync(id, new[] { Raw("E3", 3) }.AsMemory(), new StreamPosition(2));
            await PollingTestHelpers.WaitForCountAsync(received, 3);
        }
        finally
        {
            await sub.DisposeAsync();
        }

        received.Should().Equal("E1", "E2", "E3");
    }

    [Fact]
    public async Task Subscribe_Global_DeliversEveryLiveEvent_AcrossPollCycles()
    {
        var adapter = _adapter;
        var a = new StreamId($"a-{Guid.NewGuid()}");
        var b = new StreamId($"b-{Guid.NewGuid()}");
        await adapter.AppendAsync(a, new[] { Raw("A1", 1) }.AsMemory(), StreamPosition.Start);

        var received = new List<string>();
        var sub = await adapter.SubscribeAsync(StreamId.Global, StreamPosition.Start, (e, _) =>
        {
            lock (received) received.Add(e.EventType);
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();
        try
        {
            await PollingTestHelpers.WaitForCountAsync(received, 1);
            await adapter.AppendAsync(b, new[] { Raw("B1", 1) }.AsMemory(), StreamPosition.Start);
            await PollingTestHelpers.WaitForCountAsync(received, 2);
            await adapter.AppendAsync(a, new[] { Raw("A2", 2) }.AsMemory(), new StreamPosition(1));
            await PollingTestHelpers.WaitForCountAsync(received, 3);
        }
        finally
        {
            await sub.DisposeAsync();
        }

        received.Should().Equal("A1", "B1", "A2");
    }
}
