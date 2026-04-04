# Testing Projections

## Overview

Projections (read models) transform events into optimized query structures. This guide covers how to test projections thoroughly, including idempotency, event transformation, multi-stream scenarios, and edge cases.

## Key Principles

1. **Idempotency**: Apply same event multiple times produces same result
2. **Accumulation**: Multiple events accumulate state correctly
3. **Event Transformation**: Events map correctly to projection state
4. **Purity**: Apply methods are deterministic and side-effect free
5. **Consistency**: Projection state accurately reflects event history

## Projection Testing Fundamentals

### What to Test

- **Event Application**: Does Apply() correctly transform events?
- **Idempotency**: Applying same event twice produces same result
- **State Accumulation**: Multiple events accumulate correctly
- **Unknown Events**: Unrecognized events are ignored safely
- **Edge Cases**: Missing data, duplicates, out-of-order events
- **Rebuilding**: Projections can be rebuilt from events
- **Dependencies**: Mocked dependencies work correctly

## Helper Methods for Tests

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;

public static class ProjectionTestHelper
{
    public static EventEnvelope MakeEnvelope(
        StreamId? streamId = null,
        StreamPosition? position = null,
        object? @event = null,
        EventMetadata? metadata = null)
    {
        return new EventEnvelope(
            StreamId: streamId ?? new StreamId("test-stream"),
            Position: position ?? new StreamPosition(1),
            Event: @event ?? new object(),
            Metadata: metadata ?? EventMetadata.New("TestEvent")
        );
    }
    
    public static EventEnvelope MakeEnvelope<TEvent>(
        TEvent @event,
        StreamId? streamId = null,
        StreamPosition? position = null) where TEvent : notnull
    {
        return MakeEnvelope(streamId, position, @event);
    }
}
```

## Complete Order Projection Test Suite

### Domain Models

```csharp
// Events
public record OrderPlacedEvent(string OrderId, decimal Amount);
public record OrderShippedEvent(string OrderId, string TrackingCode);
public record OrderCancelledEvent(string OrderId);

// Read Models
public record OrderReadModel(
    string OrderId,
    decimal Amount,
    string? TrackingCode,
    bool IsCancelled
);

public record CustomerOrderCountReadModel(
    string CustomerId,
    int OrderCount,
    decimal TotalSpent
);

// Projection: Single Stream
public sealed class OrderListProjection : Projection<OrderReadModel>
{
    public OrderListProjection()
    {
        Current = new OrderReadModel(string.Empty, 0m, null, false);
    }
    
    protected override OrderReadModel Apply(OrderReadModel current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                new OrderReadModel(e.OrderId, e.Amount, null, false),
            
            OrderShippedEvent e =>
                current with { TrackingCode = e.TrackingCode },
            
            OrderCancelledEvent =>
                current with { IsCancelled = true },
            
            _ => current
        };
    }
}

// Projection: Multi-Stream Aggregation
public sealed class CustomerOrderCountProjection : Projection<CustomerOrderCountReadModel>
{
    public CustomerOrderCountProjection()
    {
        Current = new CustomerOrderCountReadModel(string.Empty, 0, 0m);
    }
    
    protected override CustomerOrderCountReadModel Apply(
        CustomerOrderCountReadModel current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current with
                {
                    OrderCount = current.OrderCount + 1,
                    TotalSpent = current.TotalSpent + e.Amount
                },
            
            OrderCancelledEvent =>
                current with { OrderCount = Math.Max(0, current.OrderCount - 1) },
            
            _ => current
        };
    }
}
```

### Test Class

```csharp
public class OrderListProjectionTests
{
    // --- Basic Event Application ---
    
    [Fact]
    public async Task HandleAsync_WithOrderPlaced_UpdatesReadModel()
    {
        // Arrange
        var projection = new OrderListProjection();
        var @event = new OrderPlacedEvent("ORD-001", 99.99m);
        var envelope = ProjectionTestHelper.MakeEnvelope(@event);
        
        // Act
        await projection.HandleAsync(envelope);
        
        // Assert
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(99.99m);
        projection.Current.TrackingCode.Should().BeNull();
        projection.Current.IsCancelled.Should().BeFalse();
    }
    
    [Fact]
    public async Task HandleAsync_WithOrderShipped_UpdatesTrackingCode()
    {
        // Arrange
        var projection = new OrderListProjection();
        var placed = new OrderPlacedEvent("ORD-001", 50m);
        var shipped = new OrderShippedEvent("ORD-001", "TRACK-123");
        
        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(placed));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(shipped));
        
        // Assert
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.TrackingCode.Should().Be("TRACK-123");
        projection.Current.Amount.Should().Be(50m);
    }
    
    [Fact]
    public async Task HandleAsync_WithOrderCancelled_MarksCancelled()
    {
        // Arrange
        var projection = new OrderListProjection();
        var placed = new OrderPlacedEvent("ORD-001", 100m);
        var cancelled = new OrderCancelledEvent("ORD-001");
        
        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(placed));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(cancelled));
        
        // Assert
        projection.Current.IsCancelled.Should().BeTrue();
        projection.Current.OrderId.Should().Be("ORD-001");
    }
    
    // --- State Accumulation ---
    
    [Fact]
    public async Task HandleAsync_MultipleEvents_AccumulatesState()
    {
        // Arrange
        var projection = new OrderListProjection();
        var streamId = new StreamId("order-1");
        var events = new[]
        {
            ProjectionTestHelper.MakeEnvelope(
                new OrderPlacedEvent("ORD-001", 100m),
                streamId,
                new StreamPosition(1)),
            ProjectionTestHelper.MakeEnvelope(
                new OrderShippedEvent("ORD-001", "TRACK-001"),
                streamId,
                new StreamPosition(2)),
            ProjectionTestHelper.MakeEnvelope(
                new OrderShippedEvent("ORD-001", "TRACK-002"),
                streamId,
                new StreamPosition(3))
        };
        
        // Act
        foreach (var @event in events)
        {
            await projection.HandleAsync(@event);
        }
        
        // Assert: Final state reflects all events
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(100m);
        projection.Current.TrackingCode.Should().Be("TRACK-002"); // Latest tracking
    }
    
    // --- Idempotency Tests ---
    
    [Fact]
    public async Task HandleAsync_SameEvent_AppliedTwice_ProducesSameResult()
    {
        // Arrange
        var projection1 = new OrderListProjection();
        var projection2 = new OrderListProjection();
        var @event = new OrderPlacedEvent("ORD-001", 99.99m);
        var envelope = ProjectionTestHelper.MakeEnvelope(@event);
        
        // Act: Apply once to projection1
        await projection1.HandleAsync(envelope);
        
        // Apply twice to projection2
        await projection2.HandleAsync(envelope);
        await projection2.HandleAsync(envelope);
        
        // Assert: Final state is identical
        projection1.Current.Should().Be(projection2.Current);
    }
    
    [Fact]
    public async Task HandleAsync_DuplicateEvents_IgnoredOnSecondApplication()
    {
        // Arrange
        var projection = new OrderListProjection();
        var placed1 = new OrderPlacedEvent("ORD-001", 100m);
        var placed2 = new OrderPlacedEvent("ORD-001", 100m);
        var envelope1 = ProjectionTestHelper.MakeEnvelope(placed1, position: new StreamPosition(1));
        var envelope2 = ProjectionTestHelper.MakeEnvelope(placed2, position: new StreamPosition(1));
        
        // Act: Apply the same event twice
        await projection.HandleAsync(envelope1);
        var stateAfterFirst = projection.Current;
        
        await projection.HandleAsync(envelope2);
        
        // Assert: State unchanged (idempotent)
        projection.Current.Should().Be(stateAfterFirst);
    }
    
    [Fact]
    public async Task HandleAsync_ReplayingAllEvents_ProducesSameState()
    {
        // Arrange: Build state incrementally
        var projection1 = new OrderListProjection();
        var events = new EventEnvelope[]
        {
            ProjectionTestHelper.MakeEnvelope(new OrderPlacedEvent("ORD-001", 100m)),
            ProjectionTestHelper.MakeEnvelope(new OrderShippedEvent("ORD-001", "TRACK-1")),
        };
        
        // Act: Build incrementally
        foreach (var e in events)
            await projection1.HandleAsync(e);
        
        var stateIncremental = projection1.Current;
        
        // Act: Replay from scratch
        var projection2 = new OrderListProjection();
        foreach (var e in events)
            await projection2.HandleAsync(e);
        
        var stateReplayed = projection2.Current;
        
        // Assert: States are identical
        stateIncremental.Should().Be(stateReplayed);
    }
    
    // --- Edge Cases ---
    
    [Fact]
    public async Task HandleAsync_UnknownEvent_IgnoredSafely()
    {
        // Arrange
        var projection = new OrderListProjection();
        var placed = new OrderPlacedEvent("ORD-001", 100m);
        var unknown = new { Message = "Unknown event" };
        
        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(placed));
        var stateBeforeUnknown = projection.Current;
        
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(unknown));
        
        // Assert: State unchanged
        projection.Current.Should().Be(stateBeforeUnknown);
    }
    
    [Fact]
    public async Task HandleAsync_NullEvent_HandlesSafely()
    {
        // Arrange
        var projection = new OrderListProjection();
        var stateInitial = projection.Current;
        
        // Act: Handle null gracefully
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(null!));
        
        // Assert: State unchanged
        projection.Current.Should().Be(stateInitial);
    }
    
    [Fact]
    public async Task HandleAsync_OutOfOrderEvents_AppliesInOrder()
    {
        // Arrange
        var projection = new OrderListProjection();
        
        // Events in wrong order (shipped before placed)
        var shipped = ProjectionTestHelper.MakeEnvelope(
            new OrderShippedEvent("ORD-001", "TRACK-1"),
            position: new StreamPosition(2));
        
        var placed = ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-001", 100m),
            position: new StreamPosition(1));
        
        // Act: Apply in received order (not event order)
        await projection.HandleAsync(shipped);
        await projection.HandleAsync(placed);
        
        // Assert: Final state reflects event order, not application order
        // (Note: In real systems, you'd replay from event store in correct order)
        projection.Current.OrderId.Should().Be("ORD-001");
    }
    
    [Fact]
    public async Task HandleAsync_WithMissingData_UsesDefaults()
    {
        // Arrange
        var projection = new OrderListProjection();
        var minimalEvent = new OrderPlacedEvent("ORD-001", 0m); // Zero amount
        
        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(minimalEvent));
        
        // Assert: Uses provided data, even if minimal
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(0m);
        projection.Current.TrackingCode.Should().BeNull();
    }
    
    // --- Null Safety ---
    
    [Fact]
    public async Task HandleAsync_WithNullableProperties_HandlesNull()
    {
        // Arrange
        var projection = new OrderListProjection();
        var placed = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(placed));
        
        // Assert: Nullable properties start as null
        projection.Current.TrackingCode.Should().BeNull();
    }
}

public class CustomerOrderCountProjectionTests
{
    // --- Multi-Stream Aggregation ---
    
    [Fact]
    public async Task HandleAsync_MultipleOrdersFromDifferentStreams_AggregatesCount()
    {
        // Arrange
        var projection = new CustomerOrderCountProjection();
        
        var events = new[]
        {
            ProjectionTestHelper.MakeEnvelope(
                new OrderPlacedEvent("ORD-001", 100m),
                new StreamId("customer-order-1")),
            
            ProjectionTestHelper.MakeEnvelope(
                new OrderPlacedEvent("ORD-002", 50m),
                new StreamId("customer-order-2")),
            
            ProjectionTestHelper.MakeEnvelope(
                new OrderPlacedEvent("ORD-003", 75m),
                new StreamId("customer-order-3"))
        };
        
        // Act
        foreach (var @event in events)
            await projection.HandleAsync(@event);
        
        // Assert
        projection.Current.OrderCount.Should().Be(3);
        projection.Current.TotalSpent.Should().Be(225m);
    }
    
    [Fact]
    public async Task HandleAsync_CancelledOrder_DecrementsCount()
    {
        // Arrange
        var projection = new CustomerOrderCountProjection();
        
        // Act: Place 3 orders, cancel 1
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-001", 100m)));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-002", 50m)));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-003", 75m)));
        
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderCancelledEvent("ORD-002")));
        
        // Assert
        projection.Current.OrderCount.Should().Be(2);
        projection.Current.TotalSpent.Should().Be(175m);
    }
    
    [Fact]
    public async Task HandleAsync_CancellingNonExistentOrder_NeverGoesNegative()
    {
        // Arrange
        var projection = new CustomerOrderCountProjection();
        
        // Act: Cancel without any placed orders
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderCancelledEvent("ORD-001")));
        
        // Assert: Count never goes below 0
        projection.Current.OrderCount.Should().Be(0);
    }
    
    [Fact]
    public async Task HandleAsync_MixedEvents_CalculatesTotalCorrectly()
    {
        // Arrange
        var projection = new CustomerOrderCountProjection();
        
        // Act: Place $100, $50, $75, cancel $50
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-001", 100m)));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-002", 50m)));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderPlacedEvent("ORD-003", 75m)));
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(
            new OrderCancelledEvent("ORD-002")));
        
        // Assert: Count=2, Total=$175 (100+75)
        projection.Current.OrderCount.Should().Be(2);
        projection.Current.TotalSpent.Should().Be(175m);
    }
}

public class ProjectionIdempotencyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task HandleAsync_ApplyingEventNTimes_ProducesSameResult(int times)
    {
        // Arrange
        var projections = Enumerable.Range(0, times)
            .Select(_ => new OrderListProjection())
            .ToList();
        
        var @event = new OrderPlacedEvent("ORD-001", 100m);
        var envelope = ProjectionTestHelper.MakeEnvelope(@event);
        
        // Act: Apply event N times to each projection
        foreach (var projection in projections)
        {
            for (int i = 0; i < times; i++)
                await projection.HandleAsync(envelope);
        }
        
        // Assert: All projections have identical state
        var firstState = projections[0].Current;
        projections.ForEach(p =>
            p.Current.Should().Be(firstState, 
                $"State should be identical after {times} applications")
        );
    }
}

public class ProjectionRebuildingTests
{
    [Fact]
    public async Task Projection_CanBeRebuilt_FromEventStream()
    {
        // Arrange: Create a series of events
        var events = new EventEnvelope[]
        {
            ProjectionTestHelper.MakeEnvelope(
                new OrderPlacedEvent("ORD-001", 100m),
                position: new StreamPosition(1)),
            ProjectionTestHelper.MakeEnvelope(
                new OrderShippedEvent("ORD-001", "TRACK-1"),
                position: new StreamPosition(2)),
            ProjectionTestHelper.MakeEnvelope(
                new OrderPlacedEvent("ORD-002", 50m),
                position: new StreamPosition(3)),
        };
        
        // Act: Build projection from events
        var projection = new OrderListProjection();
        foreach (var @event in events)
            await projection.HandleAsync(@event);
        
        // Assert: Projection is fully rebuilt
        projection.Current.OrderId.Should().Be("ORD-002");
        projection.Current.Amount.Should().Be(50m);
    }
    
    [Fact]
    public async Task Projection_RebuildingTwice_ProducesSameResult()
    {
        // Arrange
        var events = new EventEnvelope[]
        {
            ProjectionTestHelper.MakeEnvelope(new OrderPlacedEvent("ORD-001", 100m)),
            ProjectionTestHelper.MakeEnvelope(new OrderShippedEvent("ORD-001", "TRACK-1")),
        };
        
        // Act: Rebuild first projection
        var proj1 = new OrderListProjection();
        foreach (var @event in events)
            await proj1.HandleAsync(@event);
        
        // Act: Rebuild second projection
        var proj2 = new OrderListProjection();
        foreach (var @event in events)
            await proj2.HandleAsync(@event);
        
        // Assert
        proj1.Current.Should().Be(proj2.Current);
    }
}
```

## Testing with Mocked Dependencies

If your projection depends on external services:

```csharp
public interface IOrderService
{
    Task<Order> GetOrderAsync(string orderId);
}

public sealed class EnrichedOrderProjection : Projection<EnrichedOrderReadModel>
{
    private readonly IOrderService _orderService;
    
    public EnrichedOrderProjection(IOrderService orderService)
    {
        _orderService = orderService;
        Current = new EnrichedOrderReadModel(string.Empty, 0m, null, null);
    }
    
    protected override async ValueTask<EnrichedOrderReadModel> ApplyAsync(
        EnrichedOrderReadModel current,
        EventEnvelope @event)
    {
        if (@event.Event is OrderPlacedEvent e)
        {
            var order = await _orderService.GetOrderAsync(e.OrderId);
            return new EnrichedOrderReadModel(
                e.OrderId,
                e.Amount,
                order?.CustomerName,
                order?.Status);
        }
        
        return current;
    }
}

public class EnrichedOrderProjectionTests
{
    [Fact]
    public async Task HandleAsync_WithMockedService_EnrichesData()
    {
        // Arrange
        var mockService = new Mock<IOrderService>();
        mockService.Setup(s => s.GetOrderAsync("ORD-001"))
            .ReturnsAsync(new Order { OrderId = "ORD-001", CustomerName = "John Doe" });
        
        var projection = new EnrichedOrderProjection(mockService.Object);
        var @event = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(@event));
        
        // Assert
        projection.Current.CustomerName.Should().Be("John Doe");
        mockService.Verify(s => s.GetOrderAsync("ORD-001"), Times.Once);
    }
}
```

## Best Practices

1. **Test Apply Method**: Verify event-to-state transformation
2. **Test Idempotency**: Same event twice = same result
3. **Test Accumulation**: Multiple events combine correctly
4. **Test Edge Cases**: Null values, missing data, unknown events
5. **Use Helpers**: Create helper methods for EventEnvelope creation
6. **Isolate Projections**: Test each projection independently
7. **Test Rebuilding**: Projections can recover from event streams
8. **Mock Dependencies**: Use mocks for external services
9. **Verify Semantics**: Projection correctly implements business logic
10. **Test Consistency**: Read models accurately reflect events

## Summary

Testing projections in ZeroAlloc.EventSourcing ensures:
- Events transform correctly to read model state
- Applying the same event multiple times produces the same result (idempotency)
- Multiple events accumulate state correctly
- Unknown events are ignored safely
- Projections can be rebuilt from event streams
- Edge cases and null values are handled
- Dependencies are mockable and testable
- Read models accurately reflect event history

With these patterns, you can build reliable projections that serve as fast, accurate query models for your event-sourced system.
