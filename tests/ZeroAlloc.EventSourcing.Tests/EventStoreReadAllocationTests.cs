using ZeroAlloc.Results;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Measures what one full <see cref="EventStore.ReadAsync"/> call allocates: getting the
/// enumerator, reading every event and disposing it. The adapter, serializer and registry are
/// allocation-free, so every byte counted is library overhead.
/// </summary>
public class EventStoreReadAllocationTests
{
    private const int Iterations = 1_000;

    // The compiler-generated async iterator behind EventStore.ReadAsync is a class, so each call
    // allocates one instance: 304 B, measured on net9.0. Nothing is allocated per event, which the
    // 1- and 100-event cases pin. Tracked in issue 504; lower this to 0 when the iterator goes.
    private const int ReadCallBudgetBytes = 304;

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ReadAsync_WithAllocationFreeCollaborators_AllocatesOnlyTheIteratorPerCall(int eventCount)
    {
        var store = new EventStore(new CachedReadAdapter(eventCount), new CachedSerializer(), new ConstantRegistry());
        var id = new StreamId("alloc-stream");
        var read = 0;

        AllocationGate.AssertBudget(
            ReadCallBudgetBytes,
            Iterations,
            () => read = DrainSynchronously(store.ReadAsync(id)),
            $"EventStore.ReadAsync over {eventCount} events");

        Assert.Equal(eventCount, read);
    }

    // The fake adapter completes every step synchronously, so the iterator can be driven without
    // awaiting; a step that does not complete synchronously fails the test.
    private static int DrainSynchronously(IAsyncEnumerable<EventEnvelope> source)
    {
        var count = 0;
        var enumerator = source.GetAsyncEnumerator();
        try
        {
            while (true)
            {
                var next = enumerator.MoveNextAsync();
                if (!next.IsCompletedSuccessfully)
                    throw new InvalidOperationException("MoveNextAsync did not complete synchronously.");
                if (!next.Result)
                    return count;
                count++;
            }
        }
        finally
        {
            var disposed = enumerator.DisposeAsync();
            if (!disposed.IsCompletedSuccessfully)
                throw new InvalidOperationException("DisposeAsync did not complete synchronously.");
        }
    }

    private sealed class CachedSerializer : IEventSerializer
    {
        private static readonly object Event = new OrderPlaced("ORD-1", 1m);

        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => throw new NotSupportedException();

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType) => Event;
    }

    private sealed class ConstantRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventType, out Type? type)
        {
            type = typeof(OrderPlaced);
            return true;
        }

        public string GetTypeName(Type type) => nameof(OrderPlaced);
    }

    /// <summary>
    /// Serves the same pre-built events on every read through one reusable enumerator, so the
    /// adapter itself never allocates.
    /// </summary>
    private sealed class CachedReadAdapter : IEventStoreAdapter, IAsyncEnumerable<RawEvent>, IAsyncEnumerator<RawEvent>
    {
        private readonly RawEvent[] _events;
        private int _index = -1;

        public CachedReadAdapter(int eventCount)
        {
            _events = new RawEvent[eventCount];
            var payload = new byte[] { 1, 2, 3, 4 };
            for (var i = 0; i < eventCount; i++)
            {
                _events[i] = new RawEvent(
                    new StreamPosition(i + 1), nameof(OrderPlaced), payload, EventMetadata.New(nameof(OrderPlaced)));
            }
        }

        public RawEvent Current => _events[_index];

        public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
            => this;

        public IAsyncEnumerator<RawEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _index = -1;
            return this;
        }

        public ValueTask<bool> MoveNextAsync() => new(++_index < _events.Length);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
