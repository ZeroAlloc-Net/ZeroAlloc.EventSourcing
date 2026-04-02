using System.Text;
using FluentAssertions;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public sealed class SqlServerSubscriptionTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();
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
        }.AsMemory(), StreamPosition.Start);

        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        // Subscribe from position 3 — only "OrderDelivered" (at position 3) should arrive
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
        received[0].EventType.Should().Be("OrderDelivered");
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
}
