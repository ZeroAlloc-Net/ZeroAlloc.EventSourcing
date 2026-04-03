using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

/// <summary>
/// Integration tests demonstrating snapshot store usage patterns with aggregates.
/// These tests show how snapshots can preserve and restore aggregate state.
///
/// Note: Full snapshot-optimized repository loading (replaying from snapshot position)
/// is deferred to Phase 5.1 pending IEventStore ReadAsync position support.
/// </summary>
public sealed class SnapshotIntegrationTests
{
    [Fact]
    public async Task SnapshotStore_PreservesAggregateState()
    {
        // Arrange
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var order = new Order();
        order.SetId(new OrderId(Guid.NewGuid()));

        // Act: Simulate an aggregate being modified
        order.Place("ORD-1", 99.99m);
        var state = order.State;
        var version = new StreamPosition(1);

        // Save snapshot
        await snapshotStore.WriteAsync(new StreamId("order-1"), version, state);

        // Assert: Verify snapshot can be read back
        var loaded = await snapshotStore.ReadAsync(new StreamId("order-1"));

        loaded.Should().NotBeNull();
        loaded!.Value.Position.Should().Be(version);
        loaded.Value.State.IsPlaced.Should().BeTrue();
        loaded.Value.State.Total.Should().Be(99.99m);
    }

    [Fact]
    public async Task SnapshotStore_CanReplicateAggregateStateAcrossInstances()
    {
        // Arrange
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var aggregateId = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{aggregateId.Value}");

        // Act: First aggregate instance creates state
        var agg1 = new Order();
        agg1.SetId(aggregateId);
        agg1.Place("ORD-123", 250m);
        agg1.Ship("TRACK-456");

        // Save snapshot at position 2 (after both events)
        var snapshotPosition = new StreamPosition(2);
        await snapshotStore.WriteAsync(streamId, snapshotPosition, agg1.State);

        // Load snapshot and restore to second aggregate instance
        var snapshot = await snapshotStore.ReadAsync(streamId);

        // Assert: Snapshot preserves state correctly for replication
        snapshot.Should().NotBeNull();
        var (position, restoredState) = snapshot!.Value;

        position.Should().Be(snapshotPosition);
        restoredState.IsPlaced.Should().BeTrue();
        restoredState.IsShipped.Should().BeTrue();
        restoredState.Total.Should().Be(250m);

        // Second aggregate can be initialized with snapshot state
        // (In Phase 5.1, repository will automate this restoration)
        var agg2 = new Order();
        agg2.SetId(aggregateId);
        // Manually apply snapshot state to demonstrate the pattern
        agg2.ApplyHistoric(new OrderPlacedEvent("ORD-123", 250m), new StreamPosition(1));
        agg2.ApplyHistoric(new OrderShippedEvent("TRACK-456"), new StreamPosition(2));

        agg2.State.IsPlaced.Should().Be(restoredState.IsPlaced);
        agg2.State.IsShipped.Should().Be(restoredState.IsShipped);
        agg2.State.Total.Should().Be(restoredState.Total);
    }

    [Fact]
    public async Task SnapshotStore_LastWriteWins_OverwritesOldSnapshots()
    {
        // Arrange
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var streamId = new StreamId("order-snapshot-test");

        // Act: Create an order to get a state snapshot
        var order1 = new Order();
        order1.SetId(new OrderId(Guid.NewGuid()));
        order1.Place("ORD-1", 50m);
        var state1 = order1.State;

        await snapshotStore.WriteAsync(streamId, new StreamPosition(1), state1);

        // Overwrite with second snapshot at position 3
        var order2 = new Order();
        order2.SetId(new OrderId(Guid.NewGuid()));
        order2.Place("ORD-2", 50m);
        order2.Ship("TRACK-2");
        var state2 = order2.State;

        await snapshotStore.WriteAsync(streamId, new StreamPosition(3), state2);

        // Assert: Latest snapshot is returned
        var loaded = await snapshotStore.ReadAsync(streamId);

        loaded.Should().NotBeNull();
        loaded!.Value.Position.Value.Should().Be(3);
        loaded.Value.State.IsShipped.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotStore_NonExistentStream_ReturnsNull()
    {
        // Arrange
        var snapshotStore = new InMemorySnapshotStore<OrderState>();

        // Act
        var loaded = await snapshotStore.ReadAsync(new StreamId("non-existent"));

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotStore_MultipleStreams_IsolateSnapshots()
    {
        // Arrange
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var stream1 = new StreamId("order-1");
        var stream2 = new StreamId("order-2");

        // Act: Create two orders with different states
        var order1 = new Order();
        order1.SetId(new OrderId(Guid.NewGuid()));
        order1.Place("ORD-1", 100m);
        var state1 = order1.State;

        var order2 = new Order();
        order2.SetId(new OrderId(Guid.NewGuid()));
        order2.Place("ORD-2", 200m);
        order2.Ship("TRACK-2");
        var state2 = order2.State;

        await snapshotStore.WriteAsync(stream1, new StreamPosition(1), state1);
        await snapshotStore.WriteAsync(stream2, new StreamPosition(2), state2);

        // Assert: Each stream has its own snapshot
        var loaded1 = await snapshotStore.ReadAsync(stream1);
        var loaded2 = await snapshotStore.ReadAsync(stream2);

        loaded1.Should().NotBeNull();
        loaded1!.Value.State.Total.Should().Be(100m);
        loaded1.Value.State.IsShipped.Should().BeFalse();

        loaded2.Should().NotBeNull();
        loaded2!.Value.State.Total.Should().Be(200m);
        loaded2.Value.State.IsShipped.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotStore_CanStoreMaximalState()
    {
        // Arrange
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var streamId = new StreamId("order-maximal");

        // Act: Create and snapshot a fully-populated state
        var order = new Order();
        order.SetId(new OrderId(Guid.NewGuid()));
        order.Place("ORD-FULL", decimal.MaxValue);
        order.Ship("TRACK-UNLIMITED");

        await snapshotStore.WriteAsync(streamId, new StreamPosition(2), order.State);

        // Assert: All fields preserved
        var loaded = await snapshotStore.ReadAsync(streamId);

        loaded.Should().NotBeNull();
        var restored = loaded!.Value.State;
        restored.IsPlaced.Should().BeTrue();
        restored.IsShipped.Should().BeTrue();
        restored.Total.Should().Be(decimal.MaxValue);
    }
}
