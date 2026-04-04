using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.Testing;

/// <summary>
/// This example demonstrates how to test aggregates thoroughly.
///
/// Testing aggregates is straightforward because:
/// 1. No database needed - test in memory
/// 2. No mocks needed - just create aggregates directly
/// 3. No I/O - everything is synchronous
/// 4. Pure functions - same input always produces same output
/// </summary>

// Test domain model (simplified Order for testing)
public readonly record struct TestOrderId(Guid Value);

public partial struct TestOrderState : IAggregateState<TestOrderState>
{
    public static TestOrderState Initial => default;

    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public decimal Total { get; private set; }

    internal TestOrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };

    internal TestOrderState Apply(OrderConfirmedEvent) =>
        this with { IsConfirmed = true };
}

public record OrderPlacedEvent(decimal Total);
public record OrderConfirmedEvent;

public sealed partial class TestOrder : Aggregate<TestOrderId, TestOrderState>
{
    public void Place(decimal total)
    {
        if (total <= 0)
            throw new ArgumentException("Total must be positive");

        Raise(new OrderPlacedEvent(total));
    }

    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm unplaced order");

        Raise(new OrderConfirmedEvent());
    }

    protected override TestOrderState ApplyEvent(TestOrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        _ => state
    };
}

// ===== UNIT TESTS =====

public class AggregateTestingExamples
{
    // ===== Test 1: Happy Path =====

    /// <summary>
    /// Test the happy path: place an order, confirm it.
    /// This is the simplest test - just verify the aggregate works correctly.
    /// </summary>
    [Fact]
    public void Place_RaisesOrderPlacedEvent()
    {
        // Arrange
        var order = new TestOrder();

        // Act
        order.Place(1000m);

        // Assert
        Assert.True(order.State.IsPlaced);
        Assert.Equal(1000m, order.State.Total);

        var uncommitted = order.DequeueUncommitted();
        Assert.Single(uncommitted);
        Assert.IsType<OrderPlacedEvent>(uncommitted[0]);
    }

    /// <summary>
    /// Test confirming an order after placing it.
    /// </summary>
    [Fact]
    public void Confirm_AfterPlace_RaisesOrderConfirmedEvent()
    {
        // Arrange
        var order = new TestOrder();
        order.Place(1000m);
        order.DequeueUncommitted();  // Clear previous events

        // Act
        order.Confirm();

        // Assert
        Assert.True(order.State.IsConfirmed);

        var uncommitted = order.DequeueUncommitted();
        Assert.Single(uncommitted);
        Assert.IsType<OrderConfirmedEvent>(uncommitted[0]);
    }

    // ===== Test 2: Error Cases =====

    /// <summary>
    /// Test that placing with invalid total throws.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Place_WithInvalidTotal_Throws(decimal total)
    {
        // Arrange
        var order = new TestOrder();

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => order.Place(total));
        Assert.Contains("positive", ex.Message);
    }

    /// <summary>
    /// Test that confirming an unplaced order throws.
    /// </summary>
    [Fact]
    public void Confirm_WhenNotPlaced_Throws()
    {
        // Arrange
        var order = new TestOrder();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Contains("unplaced", ex.Message);
    }

    // ===== Test 3: State Machine =====

    /// <summary>
    /// Test that order follows state machine correctly.
    /// </summary>
    [Fact]
    public void Aggregate_FollowsCorrectStateTransitions()
    {
        // Arrange
        var order = new TestOrder();

        // Initial state
        Assert.False(order.State.IsPlaced);
        Assert.False(order.State.IsConfirmed);

        // After place
        order.Place(1000m);
        Assert.True(order.State.IsPlaced);
        Assert.False(order.State.IsConfirmed);

        // After confirm
        order.Confirm();
        Assert.True(order.State.IsPlaced);
        Assert.True(order.State.IsConfirmed);
    }

    // ===== Test 4: Event Replay =====

    /// <summary>
    /// Test that aggregate state is correctly reconstructed from events.
    /// This simulates loading an aggregate from the event store.
    /// </summary>
    [Fact]
    public void ApplyHistoric_ReconstructsState()
    {
        // Arrange: Create an order and get its events
        var originalOrder = new TestOrder();
        originalOrder.SetId(new TestOrderId(Guid.NewGuid()));
        originalOrder.Place(1500m);
        originalOrder.Confirm();

        var events = originalOrder.DequeueUncommitted();

        // Act: Reconstruct the aggregate from events
        var reconstructedOrder = new TestOrder();
        reconstructedOrder.SetId(originalOrder.Id);

        var position = StreamPosition.Start;
        foreach (var @event in events)
        {
            reconstructedOrder.ApplyHistoric(@event, position);
            position = position.Next();
        }

        // Assert: Reconstructed state matches original
        Assert.Equal(originalOrder.State.IsPlaced, reconstructedOrder.State.IsPlaced);
        Assert.Equal(originalOrder.State.IsConfirmed, reconstructedOrder.State.IsConfirmed);
        Assert.Equal(originalOrder.State.Total, reconstructedOrder.State.Total);
    }

    // ===== Test 5: Event Sourcing Properties =====

    /// <summary>
    /// Test that aggregate version is tracked correctly.
    /// Version is important for optimistic locking.
    /// </summary>
    [Fact]
    public void Version_IncrementesWithEachEvent()
    {
        // Arrange
        var order = new TestOrder();
        var startVersion = order.Version;

        // Act
        order.Place(1000m);
        var afterPlace = order.Version;

        order.Confirm();
        var afterConfirm = order.Version;

        // Assert
        Assert.Equal(0, startVersion);
        Assert.Equal(1, afterPlace);
        Assert.Equal(2, afterConfirm);
    }

    /// <summary>
    /// Test that OriginalVersion is set when loading from store.
    /// This is used for optimistic locking.
    /// </summary>
    [Fact]
    public void OriginalVersion_IsSetAfterLoadingFromStore()
    {
        // Arrange: Simulate loading from event store
        var order = new TestOrder();
        order.SetId(new TestOrderId(Guid.NewGuid()));

        // Simulate 3 events already in store
        order.ApplyHistoric(new OrderPlacedEvent(1000m), new StreamPosition(0));
        order.ApplyHistoric(new OrderConfirmedEvent(), new StreamPosition(1));

        // Act: Now modify the order (add more events)
        order.Confirm();  // This would fail because already confirmed
        // ... or add another event

        // Assert
        Assert.Equal(new StreamPosition(1), order.OriginalVersion);
        Assert.Equal(new StreamPosition(2), order.Version);
    }

    // ===== Test 6: Multiple Aggregates =====

    /// <summary>
    /// Test multiple aggregates in sequence.
    /// Each aggregate maintains its own state independently.
    /// </summary>
    [Fact]
    public void MultipleAggregates_AreIndependent()
    {
        // Arrange
        var order1 = new TestOrder();
        var order2 = new TestOrder();

        // Act
        order1.Place(1000m);
        order2.Place(2000m);

        order1.Confirm();
        // order2 is not confirmed

        // Assert
        Assert.True(order1.State.IsConfirmed);
        Assert.False(order2.State.IsConfirmed);
        Assert.Equal(1000m, order1.State.Total);
        Assert.Equal(2000m, order2.State.Total);
    }

    // ===== Test 7: Edge Cases =====

    /// <summary>
    /// Test that confirming twice throws (idempotency check).
    /// </summary>
    [Fact]
    public void Confirm_TwiceInARow_Throws()
    {
        // Arrange
        var order = new TestOrder();
        order.Place(1000m);
        order.Confirm();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Contains("already confirmed", ex.Message);
    }

    // ===== Integration Test: Simulating Event Store =====

    /// <summary>
    /// Integration test: Simulate saving and loading from event store.
    /// This tests the complete aggregate lifecycle.
    /// </summary>
    [Fact]
    public async Task AggregateLifecycle_SaveAndLoad()
    {
        // Setup: Create in-memory event store
        var eventStore = new InMemoryEventStore();
        var orderId = new TestOrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{orderId.Value}");

        // Step 1: Create and save order
        var order1 = new TestOrder();
        order1.SetId(orderId);
        order1.Place(1500m);
        order1.Confirm();

        var events = order1.DequeueUncommitted();
        var appendResult = await eventStore.AppendAsync(streamId, events, StreamPosition.Start);
        Assert.True(appendResult.IsSuccess);

        // Step 2: Load order from event store
        var order2 = new TestOrder();
        order2.SetId(orderId);

        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            order2.ApplyHistoric(envelope.Event, envelope.Position);
        }

        // Step 3: Verify loaded state matches original
        Assert.Equal(order1.State.IsPlaced, order2.State.IsPlaced);
        Assert.Equal(order1.State.IsConfirmed, order2.State.IsConfirmed);
        Assert.Equal(order1.State.Total, order2.State.Total);
    }

    // ===== Performance Test =====

    /// <summary>
    /// Test that aggregate operations are fast (no allocations).
    /// </summary>
    [Fact]
    public void Place_IsPerformant()
    {
        // Arrange
        var order = new TestOrder();
        var iterations = 1000;

        // Act
        var startTime = DateTime.UtcNow;
        for (int i = 0; i < iterations; i++)
        {
            var o = new TestOrder();
            o.Place(1000m);
        }
        var elapsed = DateTime.UtcNow - startTime;

        // Assert: Should complete in < 100ms (very fast)
        Assert.True(elapsed.TotalMilliseconds < 100, $"Too slow: {elapsed.TotalMilliseconds}ms for {iterations} iterations");
    }
}
