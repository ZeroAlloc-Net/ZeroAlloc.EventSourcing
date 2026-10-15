using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.Results;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

/// <summary>
/// Measures what <see cref="AggregateRepository{TAggregate,TId}.SaveAsync"/> itself allocates.
/// The store completes synchronously without allocating, so every byte counted is repository
/// or aggregate overhead.
/// </summary>
public class AggregateRepositoryAllocationTests
{
    private const int Iterations = 1_000;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SaveAsync_WithAnAllocationFreeStore_DoesNotAllocate(int eventsPerSave)
    {
        var store = new NoOpEventStore();
        var streamId = new StreamId("order-alloc");
        var repo = new AggregateRepository<Order, OrderId>(store, () => new Order(), _ => streamId);
        var id = new OrderId(Guid.NewGuid());
        var order = new Order();
        order.SetId(id);
        var placed = new OrderPlacedEvent("ORD-1", 1m);
        var saves = 0;

        // Each call records the events it saves, since a save with nothing uncommitted would not
        // reach the store; recording allocates nothing either. The store completes synchronously,
        // so the save does too, and the gate fails a call that does not. The budget is exact.
        AllocationGate.AssertBudgetValueTask(
            0,
            Iterations,
            () =>
            {
                for (var e = 0; e < eventsPerSave; e++)
                    order.Record(placed);
                saves++;
                return repo.SaveAsync(order, id);
            },
            $"AggregateRepository.SaveAsync of {eventsPerSave} events");

        store.LastCount.Should().Be(eventsPerSave);
        order.OriginalVersion.Value.Should().Be(saves * eventsPerSave);
    }

    [Fact]
    public async Task SaveAsync_PassesTheUncommittedEventsInOrder_AndClearsTheQueue()
    {
        var store = new NoOpEventStore();
        var repo = new AggregateRepository<Order, OrderId>(store, () => new Order(), _ => new StreamId("order-1"));
        var id = new OrderId(Guid.NewGuid());
        var order = new Order();
        order.Place("ORD-1", 5m);
        order.Ship("TRACK-1");

        var result = await repo.SaveAsync(order, id);

        result.IsSuccess.Should().BeTrue();
        store.Seen.Should().HaveCount(2);
        store.Seen[0].Should().BeOfType<OrderPlacedEvent>();
        store.Seen[1].Should().BeOfType<OrderShippedEvent>();
        order.DequeueUncommitted().Length.Should().Be(0);
        order.OriginalVersion.Value.Should().Be(2);
    }

    private sealed class NoOpEventStore : IEventStore
    {
        public int LastCount { get; private set; }

        public List<object> Seen { get; } = [];

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<object> events, StreamPosition expectedVersion, CancellationToken ct = default)
        {
            LastCount = events.Length;
            if (Seen.Count == 0)
                Seen.AddRange(events.Span);
            return ValueTask.FromResult(Result<AppendResult, StoreError>.Success(
                new AppendResult(id, new StreamPosition(expectedVersion.Value + events.Length))));
        }

        public IAsyncEnumerable<EventEnvelope> ReadAsync(StreamId id, StreamPosition from = default, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<EventEnvelope, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
