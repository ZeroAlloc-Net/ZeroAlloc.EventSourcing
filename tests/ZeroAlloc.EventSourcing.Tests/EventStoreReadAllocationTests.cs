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

    // A read allocates nothing: ReadAsync returns a struct reader whose enumeration state is
    // pooled per thread, issue 512. It was 96 B for the reader object while ReadAsync returned an
    // IAsyncEnumerable, and 304 B while that was a compiler-generated async iterator, issue 504.
    // Nothing is allocated per event either, which the 1- and 100-event cases pin.
    private const int ReadCallBudgetBytes = 0;

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ReadAsync_WithAllocationFreeCollaborators_AllocatesNothing(int eventCount)
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

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ReadBatchAsync_WithAllocationFreeCollaborators_AllocatesNothing(int eventCount)
    {
        var store = new EventStore(new CachedReadAdapter(eventCount), new CachedSerializer(), new ConstantRegistry());
        var id = new StreamId("alloc-stream");
        var read = 0;

        AllocationGate.AssertBudget(
            ReadCallBudgetBytes,
            Iterations,
            () => read = DrainSynchronously(store.ReadBatchAsync(id, StreamPosition.Start, eventCount)),
            $"EventStore.ReadBatchAsync over {eventCount} events");

        Assert.Equal(eventCount, read);
    }

    /// <summary>
    /// Reads that overlap on one thread, as a projection that reads another stream while it handles
    /// an event does, each rent their own pooled state and allocate nothing either.
    /// </summary>
    [Fact]
    public void OverlappingReads_OnOneThread_AllocateNothing()
    {
        var outerStore = new EventStore(new CachedReadAdapter(3), new CachedSerializer(), new ConstantRegistry());
        var innerStore = new EventStore(new CachedReadAdapter(3), new CachedSerializer(), new ConstantRegistry());
        var id = new StreamId("alloc-stream");
        var read = 0;

        AllocationGate.AssertBudget(
            ReadCallBudgetBytes,
            Iterations,
            () => read = DrainNested(outerStore.ReadAsync(id), innerStore.ReadAsync(id)),
            "two overlapping EventStore.ReadAsync enumerations");

        Assert.Equal(6, read);
    }

    private static int DrainNested(EventStreamReader outer, EventStreamReader inner)
    {
        var outerEnumerator = outer.GetAsyncEnumerator();
        var innerEnumerator = inner.GetAsyncEnumerator();
        var count = 0;
        try
        {
            while (Step(outerEnumerator))
                count++;
            while (Step(innerEnumerator))
                count++;
            return count;
        }
        finally
        {
            Complete(innerEnumerator.DisposeAsync());
            Complete(outerEnumerator.DisposeAsync());
        }

        static bool Step(EventStreamReader.Enumerator enumerator)
        {
            var next = enumerator.MoveNextAsync();
            if (!next.IsCompletedSuccessfully)
                throw new InvalidOperationException("MoveNextAsync did not complete synchronously.");
            return next.Result;
        }

        static void Complete(ValueTask task)
        {
            if (!task.IsCompletedSuccessfully)
                throw new InvalidOperationException("DisposeAsync did not complete synchronously.");
        }
    }

    // The fake adapter completes every step synchronously, so the reader can be driven without
    // awaiting; a step that does not complete synchronously fails the test.
    private static int DrainSynchronously(EventStreamReader source)
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

        public IAsyncEnumerable<RawEvent> ReadBatchAsync(StreamId id, StreamPosition from, int maxCount, CancellationToken ct = default)
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
