using System.Text;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Examples.Advanced;
using ZeroAlloc.Results;

// The C# in docs/advanced/custom-event-store.md that is not part of
// docs/examples/04-advanced/CustomEventStore.cs, copied as it appears there. When a snippet
// changes in the docs, change it here as well. See issue #402.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.CustomEventStoreDocs;

// --- snippet: "Testing Your Adapter" ---
public class DictionaryEventStoreAdapterTests
{
    private readonly DictionaryEventStoreAdapter _adapter = new();

    // EventStore normally builds the RawEvents; a test of the adapter alone builds them itself
    private static RawEvent Raw(string eventType, string json)
        => new(StreamPosition.Start, eventType, Encoding.UTF8.GetBytes(json), EventMetadata.New(eventType));

    [Fact]
    public async Task AppendAsync_AppendsEvents()
    {
        var streamId = new StreamId("order-123");

        var result = await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderPlaced", "{\"total\":1000}") },
            StreamPosition.Start);

        // Positions are 1-based: one event appended to an empty stream sits at 1
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.NextExpectedVersion.Value);
    }

    [Fact]
    public async Task AppendAsync_DetectsOptimisticLockConflict()
    {
        var streamId = new StreamId("order-456");

        // First append succeeds
        await _adapter.AppendAsync(streamId, new[] { Raw("OrderPlaced", "{}") }, StreamPosition.Start);

        // Second append with wrong version fails
        var result = await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderShipped", "{}") },
            StreamPosition.Start);  // Wrong: the stream is at version 1 now

        Assert.False(result.IsSuccess);
        Assert.Equal("CONFLICT", result.Error.Code);
    }

    [Fact]
    public async Task ReadAsync_ReadsAllEventsInOrder()
    {
        var streamId = new StreamId("order-789");
        await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderPlaced", "{\"total\":1000}"), Raw("OrderShipped", "{\"tracking\":\"ABC123\"}") },
            StreamPosition.Start);

        var readEvents = new List<RawEvent>();
        await foreach (var @event in _adapter.ReadAsync(streamId, StreamPosition.Start))
        {
            readEvents.Add(@event);
        }

        Assert.Equal(new[] { "OrderPlaced", "OrderShipped" }, readEvents.Select(e => e.EventType));
        Assert.Equal(new long[] { 1, 2 }, readEvents.Select(e => e.Position.Value));
    }

    [Fact]
    public async Task ReadAsync_FromAPosition_ExcludesThatPosition()
    {
        var streamId = new StreamId("order-101");
        await _adapter.AppendAsync(
            streamId,
            new[] { Raw("OrderPlaced", "{}"), Raw("OrderShipped", "{}") },
            StreamPosition.Start);

        var readEvents = new List<RawEvent>();
        await foreach (var @event in _adapter.ReadAsync(streamId, new StreamPosition(1)))
        {
            readEvents.Add(@event);
        }

        Assert.Equal("OrderShipped", Assert.Single(readEvents).EventType);
    }
}
// --- end snippet ---

/// <summary>
/// "Subscriptions for a database": the SubscribeAsync snippet needs an adapter around it. This
/// one delegates the rest to <see cref="DictionaryEventStoreAdapter"/>.
/// </summary>
public sealed class PollingAdapter : IEventStoreAdapter
{
    private readonly DictionaryEventStoreAdapter _inner = new();

    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
        => _inner.AppendAsync(id, events, expectedVersion, ct);

    public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        => _inner.ReadAsync(id, from, ct);

    // --- snippet: "Subscriptions for a database" ---
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        // Catches up from 'from', then polls ReadAsync until the subscription is disposed
        var subscription = new PollingEventSubscription(
            this, id, from, handler, PollingEventSubscription.DefaultPollInterval);
        return ValueTask.FromResult<IEventSubscription>(subscription);
    }
    // --- end snippet ---
}

public sealed class PollingAdapterTests
{
    [Fact]
    public async Task Subscription_CatchesUpOnStart()
    {
        var adapter = new PollingAdapter();
        var id = new StreamId("order-1");
        await adapter.AppendAsync(
            id,
            new[] { new RawEvent(StreamPosition.Start, "OrderPlaced", new byte[] { 1 }, EventMetadata.New("OrderPlaced")) },
            StreamPosition.Start);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.TrySetResult(e.EventType);
            return ValueTask.CompletedTask;
        });
        await subscription.StartAsync();

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("OrderPlaced");
    }
}
