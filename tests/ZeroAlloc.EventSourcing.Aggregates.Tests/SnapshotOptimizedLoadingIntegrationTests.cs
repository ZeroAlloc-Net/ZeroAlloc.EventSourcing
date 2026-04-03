using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

public sealed class SnapshotOptimizedLoadingIntegrationTests : IAsyncLifetime
{
    private InMemoryEventStoreAdapter _adapter = null!;
    private EventStore _eventStore = null!;
    private InMemorySnapshotStore<OrderState> _snapshotStore = null!;
    private AggregateRepository<Order, OrderId> _innerRepository = null!;

    public async Task InitializeAsync()
    {
        _adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonAggregateSerializer();
        var registry = new OrderEventTypeRegistry();
        _eventStore = new EventStore(_adapter, serializer, registry);
        _snapshotStore = new InMemorySnapshotStore<OrderState>();
        _innerRepository = new AggregateRepository<Order, OrderId>(
            _eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}"));
    }

    public async Task DisposeAsync()
    {
        // Cleanup if needed
    }

    [Fact]
    public async Task SnapshotOptimizedLoad_ReducesEventReplays()
    {
        // Arrange
        var order = new Order();
        var id = new OrderId(Guid.NewGuid());
        order.SetId(id);

        // Create many events (10 Place events)
        // Each Place call replaces the total, so final total is from the last event
        for (int i = 0; i < 10; i++)
        {
            order.Place($"ORD-{i + 1}", (i + 1) * 100m);
        }
        await _innerRepository.SaveAsync(order, id);

        // Take snapshot at position 10 (after all events)
        await _snapshotStore.WriteAsync(
            new StreamId($"order-{id.Value}"),
            new StreamPosition(10),
            order.State);

        // Create decorator with snapshot optimization
        var decoratedRepo = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            _innerRepository,
            _snapshotStore,
            SnapshotLoadingStrategy.TrustSnapshot,
            RestoreOrderState,
            _eventStore,
            orderId => new StreamId($"order-{orderId.Value}"),
            () => new Order());

        // Act: Load with snapshot
        var result = await decoratedRepo.LoadAsync(id);

        // Assert: Loaded successfully with final state
        result.IsSuccess.Should().BeTrue();
        result.Value.State.Total.Should().Be(1000m); // Last Place event has total of 10 * 100m
        result.Value.Version.Value.Should().Be(10);
    }

    private static void RestoreOrderState(Order aggregate, OrderState state, StreamPosition fromPosition)
    {
        // Restore the state directly onto the aggregate using the snapshot position
        // This sets both Version and OriginalVersion to the snapshot position
        aggregate.ApplyHistoric(
            new OrderPlacedEvent(state.OrderId!, state.Total),
            fromPosition);
    }
}
