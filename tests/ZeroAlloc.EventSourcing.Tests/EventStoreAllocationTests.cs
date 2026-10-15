using AwesomeAssertions;
using ZeroAlloc.Results;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Measures what <see cref="EventStore.AppendAsync"/> itself allocates. The adapter, serializer
/// and registry are allocation-free, so every byte counted is library overhead.
/// </summary>
public class EventStoreAllocationTests
{
    private const int Iterations = 1_000;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void AppendAsync_WithAllocationFreeCollaborators_DoesNotAllocate(int eventCount)
    {
        var adapter = new NoOpAdapter();
        var store = new EventStore(adapter, new CachedSerializer(), new ConstantRegistry());
        var id = new StreamId("alloc-stream");
        var events = new object[eventCount];
        for (var i = 0; i < events.Length; i++)
            events[i] = new OrderPlaced("ORD-1", 1m);
        var memory = new ReadOnlyMemory<object>(events);

        // The adapter completes synchronously, so the append does too; the gate fails a call that
        // does not. The budget is exact: an append allocates nothing of its own.
        AllocationGate.AssertBudgetValueTask(
            0,
            Iterations,
            () => store.AppendAsync(id, memory, StreamPosition.Start),
            $"EventStore.AppendAsync of {eventCount} events");

        adapter.LastCount.Should().Be(eventCount);
    }

    [Fact]
    public async Task AppendAsync_PassesEveryEventToTheAdapterInOrder()
    {
        var adapter = new NoOpAdapter();
        var store = new EventStore(adapter, new CachedSerializer(), new ConstantRegistry());
        var events = new object[] { new OrderPlaced("A", 1m), new OrderPlaced("B", 2m), new OrderPlaced("C", 3m) };

        var result = await store.AppendAsync(new StreamId("s"), events.AsMemory(), new StreamPosition(4));

        result.IsSuccess.Should().BeTrue();
        adapter.Seen.Select(e => e.Position.Value).Should().Equal(5, 6, 7);
        adapter.Seen.Should().OnlyContain(e => e.EventType == nameof(OrderPlaced));
    }

    [Fact]
    public async Task AppendAsync_WhenTheAdapterThrows_PropagatesTheException()
    {
        var store = new EventStore(new ThrowingAdapter(), new CachedSerializer(), new ConstantRegistry());

        var act = async () => await store.AppendAsync(new StreamId("s"), new object[] { new OrderPlaced("A", 1m) }.AsMemory(), StreamPosition.Start);

        await act.Should().ThrowAsync<InvalidOperationException>();
        // A later append still works: the rented buffer went back to the pool cleanly.
        var adapter = new NoOpAdapter();
        var ok = new EventStore(adapter, new CachedSerializer(), new ConstantRegistry());
        (await ok.AppendAsync(new StreamId("s"), new object[] { new OrderPlaced("A", 1m) }.AsMemory(), StreamPosition.Start))
            .IsSuccess.Should().BeTrue();
    }

    private sealed class CachedSerializer : IEventSerializer
    {
        private static readonly ReadOnlyMemory<byte> Payload = new byte[] { 1, 2, 3, 4 };

        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull => Payload;

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType) => throw new NotSupportedException();
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

    private sealed class NoOpAdapter : IEventStoreAdapter
    {
        public int LastCount { get; private set; }

        public List<RawEvent> Seen { get; } = [];

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
        {
            LastCount = events.Length;
            if (Seen.Count == 0 && events.Length <= 3)
                Seen.AddRange(events.Span);
            return ValueTask.FromResult(Result<AppendResult, StoreError>.Success(
                new AppendResult(id, new StreamPosition(expectedVersion.Value + events.Length))));
        }

        public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingAdapter : IEventStoreAdapter
    {
        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new InvalidOperationException("adapter failed");

        public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
