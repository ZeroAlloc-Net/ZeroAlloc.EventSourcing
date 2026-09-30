using System.Text;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Unit tests for <see cref="PollingEventSubscription"/> using an inline fake adapter —
/// no Testcontainers required.
/// </summary>
public sealed class PollingEventSubscriptionTests
{
    private static RawEvent MakeRaw(string eventType = "TestEvent")
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        return new RawEvent(new StreamPosition(1), eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    /// <summary>
    /// Fake adapter that yields a single pre-built event on the first ReadAsync call.
    /// Subsequent calls yield nothing, simulating an empty live tail.
    /// </summary>
    private sealed class SingleEventAdapter : IEventStoreAdapter
    {
        private readonly RawEvent _event;
        private int _readCount;

        public SingleEventAdapter(RawEvent @event) => _event = @event;

        public async IAsyncEnumerable<RawEvent> ReadAsync(
            StreamId id,
            StreamPosition from,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_readCount++ == 0)
                yield return _event;
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task HandlerException_PropagatesViaDisposeAsync()
    {
        var id = new StreamId("test-stream");
        var adapter = new SingleEventAdapter(MakeRaw());

        var sub = new PollingEventSubscription(
            adapter, id, StreamPosition.Start,
            (_, _) => ValueTask.FromException(new InvalidOperationException("handler boom")),
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();

        // Give the background task a moment to hit the handler and fault.
        await Task.Delay(200);

        var act = async () => await sub.DisposeAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler boom");
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
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await adapter.AppendAsync(id, new[] { Raw("E1", 1) }.AsMemory(), StreamPosition.Start);

        var received = new List<string>();
        var sub = new PollingEventSubscription(adapter, id, StreamPosition.Start, (e, _) =>
        {
            lock (received) received.Add(e.EventType);
            return ValueTask.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));
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
        var adapter = new InMemoryEventStoreAdapter();
        var a = new StreamId($"a-{Guid.NewGuid()}");
        var b = new StreamId($"b-{Guid.NewGuid()}");
        await adapter.AppendAsync(a, new[] { Raw("A1", 1) }.AsMemory(), StreamPosition.Start);

        var received = new List<string>();
        var sub = new PollingEventSubscription(adapter, StreamId.Global, StreamPosition.Start, (e, _) =>
        {
            lock (received) received.Add(e.EventType);
            return ValueTask.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));
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
