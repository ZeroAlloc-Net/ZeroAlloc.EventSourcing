# Testing Aggregates

## Overview

Testing aggregates in ZeroAlloc.EventSourcing is about verifying that your domain entities correctly raise events and apply state transitions. This guide covers unit testing strategies for aggregates using xUnit and FluentAssertions.

## Key Principles

1. **Arrange-Act-Assert**: Structure tests with clear setup, action, and verification phases
2. **Test Events, Not State**: Verify the events raised, not just the final state
3. **Purity**: ApplyEvent methods must be deterministic and side-effect free
4. **State Verification**: Ensure final aggregate state matches expectations
5. **Command Validation**: Test both happy paths and sad paths (validation failures)

## Aggregate Testing Fundamentals

### What to Test

- **Command processing**: Does the aggregate correctly handle commands?
- **Event raising**: Are the right events raised for a given command?
- **State application**: Does ApplyEvent correctly update state?
- **Validation**: Are invalid inputs rejected appropriately?
- **State transitions**: Does the aggregate enforce valid state flows?

### Testing Pattern

```csharp
[Fact]
public void CommandName_Condition_ExpectedBehavior()
{
    // Arrange: Set up the aggregate
    var aggregate = new Order();
    
    // Act: Execute the command
    aggregate.PlaceOrder("ORD-001", 99.99m);
    
    // Assert: Verify the results
    var events = aggregate.DequeueUncommitted();
    events.Should().HaveLength(1);
    events[0].Should().BeOfType<OrderPlacedEvent>();
    aggregate.State.IsPlaced.Should().BeTrue();
}
```

## Complete Order Aggregate Test Suite

### Domain Model

```csharp
public readonly record struct OrderId(Guid Value);

// Events
public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderConfirmedEvent();
public record OrderShippedEvent(string TrackingNumber);
public record OrderCancelledEvent(string Reason);

// Aggregate State
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public bool IsCancelled { get; private set; }
    
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
    
    internal OrderState Apply(OrderConfirmedEvent _) =>
        this with { IsConfirmed = true };
    
    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
    
    internal OrderState Apply(OrderCancelledEvent e) =>
        this with { IsCancelled = true };
}

// Aggregate
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void PlaceOrder(string orderId, decimal total)
    {
        if (total <= 0)
            throw new ArgumentException("Total must be positive");
        
        Raise(new OrderPlacedEvent(orderId, total));
    }
    
    public void ConfirmOrder()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Order must be placed before confirming");
        
        if (State.IsConfirmed)
            throw new InvalidOperationException("Order is already confirmed");
        
        Raise(new OrderConfirmedEvent());
    }
    
    public void ShipOrder(string tracking)
    {
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Order must be confirmed before shipping");
        
        if (string.IsNullOrWhiteSpace(tracking))
            throw new ArgumentException("Tracking number is required");
        
        Raise(new OrderShippedEvent(tracking));
    }
    
    public void Cancel(string reason)
    {
        if (State.IsCancelled)
            throw new InvalidOperationException("Order is already cancelled");
        
        if (State.IsShipped)
            throw new InvalidOperationException("Cannot cancel a shipped order");
        
        Raise(new OrderCancelledEvent(reason));
    }
    
    public void SetId(OrderId id) => Id = id;
    
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        OrderCancelledEvent e => state.Apply(e),
        _ => state
    };
}
```

### Test Class

```csharp
public class OrderAggregateTests
{
    // --- Initialization Tests ---
    
    [Fact]
    public void NewOrder_HasInitialState()
    {
        // Arrange & Act
        var order = new Order();
        
        // Assert
        order.Version.Should().Be(StreamPosition.Start);
        order.OriginalVersion.Should().Be(StreamPosition.Start);
        order.State.IsPlaced.Should().BeFalse();
        order.State.IsConfirmed.Should().BeFalse();
        order.State.IsShipped.Should().BeFalse();
        order.State.Total.Should().Be(0m);
        order.DequeueUncommitted().Should().BeEmpty();
    }
    
    // --- Happy Path Tests ---
    
    [Fact]
    public void PlaceOrder_WithValidInput_RaisesOrderPlacedEvent()
    {
        // Arrange
        var order = new Order();
        var orderId = "ORD-001";
        var total = 99.99m;
        
        // Act
        order.PlaceOrder(orderId, total);
        
        // Assert
        var events = order.DequeueUncommitted();
        events.Should().HaveLength(1);
        events[0].Should().BeOfType<OrderPlacedEvent>();
        
        var e = (OrderPlacedEvent)events[0];
        e.OrderId.Should().Be(orderId);
        e.Total.Should().Be(total);
        
        order.State.IsPlaced.Should().BeTrue();
        order.State.Total.Should().Be(total);
    }
    
    [Fact]
    public void ConfirmOrder_AfterPlaced_RaisesOrderConfirmedEvent()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.DequeueUncommitted(); // Clear pending
        
        // Act
        order.ConfirmOrder();
        
        // Assert
        var events = order.DequeueUncommitted();
        events.Should().HaveLength(1);
        events[0].Should().BeOfType<OrderConfirmedEvent>();
        
        order.State.IsConfirmed.Should().BeTrue();
    }
    
    [Fact]
    public void ShipOrder_WithValidTracking_RaisesOrderShippedEvent()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.ConfirmOrder();
        order.DequeueUncommitted(); // Clear pending
        
        var tracking = "TRACK-123456";
        
        // Act
        order.ShipOrder(tracking);
        
        // Assert
        var events = order.DequeueUncommitted();
        events.Should().HaveLength(1);
        events[0].Should().BeOfType<OrderShippedEvent>();
        
        var e = (OrderShippedEvent)events[0];
        e.TrackingNumber.Should().Be(tracking);
        
        order.State.IsShipped.Should().BeTrue();
        order.State.TrackingNumber.Should().Be(tracking);
    }
    
    [Fact]
    public void CompleteOrderFlow_PlaceConfirmShip_AllEventsRaised()
    {
        // Arrange
        var order = new Order();
        
        // Act
        order.PlaceOrder("ORD-001", 100m);
        order.ConfirmOrder();
        order.ShipOrder("TRACK-789");
        
        // Assert
        var events = order.DequeueUncommitted();
        events.Should().HaveLength(3);
        events[0].Should().BeOfType<OrderPlacedEvent>();
        events[1].Should().BeOfType<OrderConfirmedEvent>();
        events[2].Should().BeOfType<OrderShippedEvent>();
        
        order.State.IsPlaced.Should().BeTrue();
        order.State.IsConfirmed.Should().BeTrue();
        order.State.IsShipped.Should().BeTrue();
    }
    
    // --- Sad Path Tests (Validation) ---
    
    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlaceOrder_WithInvalidTotal_ThrowsArgumentException(decimal invalidTotal)
    {
        // Arrange
        var order = new Order();
        
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            order.PlaceOrder("ORD-001", invalidTotal)
        );
        
        exception.Message.Should().Contain("positive");
        order.State.IsPlaced.Should().BeFalse();
        order.DequeueUncommitted().Should().BeEmpty();
    }
    
    [Fact]
    public void ConfirmOrder_WhenNotPlaced_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = new Order();
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.ConfirmOrder()
        );
        
        exception.Message.Should().Contain("placed");
        order.State.IsConfirmed.Should().BeFalse();
    }
    
    [Fact]
    public void ConfirmOrder_WhenAlreadyConfirmed_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.ConfirmOrder();
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.ConfirmOrder()
        );
        
        exception.Message.Should().Contain("already confirmed");
    }
    
    [Fact]
    public void ShipOrder_WhenNotConfirmed_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.ShipOrder("TRACK-123")
        );
        
        exception.Message.Should().Contain("confirmed");
    }
    
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShipOrder_WithInvalidTracking_ThrowsArgumentException(string invalidTracking)
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.ConfirmOrder();
        
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            order.ShipOrder(invalidTracking)
        );
        
        exception.Message.Should().Contain("Tracking");
        order.State.IsShipped.Should().BeFalse();
    }
    
    // --- Edge Cases ---
    
    [Fact]
    public void CancelOrder_BeforePlaced_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = new Order();
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.Cancel("Wrong decision")
        );
        
        exception.Message.Should().Contain("cancelled");
    }
    
    [Fact]
    public void CancelOrder_AfterShipped_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.ConfirmOrder();
        order.ShipOrder("TRACK-123");
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.Cancel("Customer requested")
        );
        
        exception.Message.Should().Contain("shipped");
    }
    
    [Fact]
    public void CancelOrder_WhenAlreadyCancelled_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.Cancel("Changed mind");
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.Cancel("Duplicate cancel")
        );
        
        exception.Message.Should().Contain("already cancelled");
    }
    
    [Fact]
    public void CancelOrder_BeforeShipping_Succeeds()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.ConfirmOrder();
        order.DequeueUncommitted(); // Clear pending
        
        // Act
        order.Cancel("Customer requested");
        
        // Assert
        var events = order.DequeueUncommitted();
        events.Should().HaveLength(1);
        events[0].Should().BeOfType<OrderCancelledEvent>();
        
        order.State.IsCancelled.Should().BeTrue();
    }
    
    // --- ApplyEvent Purity Tests ---
    
    [Fact]
    public void ApplyHistoric_DesNotAddToUncommitted()
    {
        // Arrange
        var order = new Order();
        
        // Act
        order.ApplyHistoric(new OrderPlacedEvent("ORD-001", 99.99m), new StreamPosition(1));
        
        // Assert
        order.State.IsPlaced.Should().BeTrue();
        order.Version.Value.Should().Be(1);
        order.DequeueUncommitted().Should().BeEmpty();
    }
    
    [Fact]
    public void ApplyEvent_IsDeterministic_SameInputProducesSameOutput()
    {
        // Arrange
        var order1 = new Order();
        var order2 = new Order();
        var placedEvent = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act
        order1.ApplyHistoric(placedEvent, new StreamPosition(1));
        order2.ApplyHistoric(placedEvent, new StreamPosition(1));
        
        // Assert
        order1.State.Should().Be(order2.State);
    }
    
    [Fact]
    public void UnknownEvent_DoesNotChangeState()
    {
        // Arrange
        var order = new Order();
        var unknownEvent = new { Message = "Unknown" };
        
        // Act
        order.ApplyHistoric(unknownEvent, new StreamPosition(1));
        
        // Assert
        order.State.IsPlaced.Should().BeFalse();
        order.State.IsConfirmed.Should().BeFalse();
        order.State.IsShipped.Should().BeFalse();
    }
    
    // --- Idempotency Tests ---
    
    [Fact]
    public void DequeueUncommitted_CalledTwice_SecondReturnsEmpty()
    {
        // Arrange
        var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        
        // Act
        var firstDequeue = order.DequeueUncommitted();
        var secondDequeue = order.DequeueUncommitted();
        
        // Assert
        firstDequeue.Should().HaveLength(1);
        secondDequeue.Should().BeEmpty();
    }
    
    [Fact]
    public void MultipleCommands_AllEventsQueuedThenCleared()
    {
        // Arrange
        var order = new Order();
        
        // Act
        order.PlaceOrder("ORD-001", 100m);
        order.ConfirmOrder();
        
        var firstDequeue = order.DequeueUncommitted();
        
        order.ShipOrder("TRACK-123");
        var secondDequeue = order.DequeueUncommitted();
        
        // Assert
        firstDequeue.Should().HaveLength(2);
        secondDequeue.Should().HaveLength(1);
    }
}
```

## xUnit Testing Patterns

### Using Facts vs Theories

```csharp
// Fact: Single scenario with fixed values
[Fact]
public void PlaceOrder_ValidInput_Succeeds()
{
    var order = new Order();
    order.PlaceOrder("ORD-001", 50m);
    order.State.IsPlaced.Should().BeTrue();
}

// Theory: Multiple scenarios with varying inputs
[Theory]
[InlineData(-10)]
[InlineData(0)]
[InlineData(-1)]
public void PlaceOrder_InvalidTotal_Throws(decimal invalidTotal)
{
    var order = new Order();
    Assert.Throws<ArgumentException>(() => 
        order.PlaceOrder("ORD-001", invalidTotal)
    );
}
```

### Using Fixtures

```csharp
public class OrderTestFixture
{
    public Order CreateOrder()
    {
        return new Order();
    }
    
    public Order CreatePlacedOrder(decimal total = 100m)
    {
        var order = new Order();
        order.PlaceOrder("ORD-001", total);
        return order;
    }
    
    public Order CreateConfirmedOrder(decimal total = 100m)
    {
        var order = CreatePlacedOrder(total);
        order.ConfirmOrder();
        return order;
    }
}

public class OrderAggregateWithFixtureTests : IClassFixture<OrderTestFixture>
{
    private readonly OrderTestFixture _fixture;
    
    public OrderAggregateWithFixtureTests(OrderTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public void PlaceOrder_WithFixture_Works()
    {
        var order = _fixture.CreatePlacedOrder(75m);
        order.State.Total.Should().Be(75m);
    }
}
```

## Testing State Transitions

State transition testing ensures the aggregate enforces valid state flows:

```csharp
[Fact]
public void ValidStateTransitions_Succeed()
{
    // Initial -> Placed
    var order = new Order();
    order.PlaceOrder("ORD-001", 50m);
    order.State.IsPlaced.Should().BeTrue();
    
    // Placed -> Confirmed
    order.ConfirmOrder();
    order.State.IsConfirmed.Should().BeTrue();
    
    // Confirmed -> Shipped
    order.ShipOrder("TRACK-123");
    order.State.IsShipped.Should().BeTrue();
}

[Fact]
public void InvalidStateTransition_Throws()
{
    // Cannot confirm without placing
    var order = new Order();
    Assert.Throws<InvalidOperationException>(() => order.ConfirmOrder());
    
    // Cannot ship without confirming
    var order2 = new Order();
    order2.PlaceOrder("ORD-001", 50m);
    Assert.Throws<InvalidOperationException>(() => order2.ShipOrder("TRACK-123"));
}
```

## Testing Command Validation

Commands should validate inputs and enforce business rules:

```csharp
[Theory]
[InlineData(0)]
[InlineData(-1)]
[InlineData(-100)]
public void PlaceOrder_WithNegativeOrZeroTotal_Throws(decimal invalidTotal)
{
    var order = new Order();
    var ex = Assert.Throws<ArgumentException>(() =>
        order.PlaceOrder("ORD-001", invalidTotal)
    );
    
    ex.Message.Should().Contain("positive");
    order.State.IsPlaced.Should().BeFalse();
    order.DequeueUncommitted().Should().BeEmpty();
}

[Theory]
[InlineData("")]
[InlineData(null)]
[InlineData("   ")]
public void ShipOrder_WithInvalidTracking_Throws(string invalidTracking)
{
    var order = new Order();
    order.PlaceOrder("ORD-001", 50m);
    order.ConfirmOrder();
    
    var ex = Assert.Throws<ArgumentException>(() =>
        order.ShipOrder(invalidTracking)
    );
    
    ex.Message.Should().Contain("Tracking");
}
```

## Testing Event Versioning

When events evolve, test backward compatibility:

```csharp
// Old event version
public record OrderPlacedEventV1(string OrderId, decimal Total);

// New event version with additional field
public record OrderPlacedEventV2(
    string OrderId, 
    decimal Total, 
    string Currency = "USD"
);

[Fact]
public void ApplyEvent_HandlesOldEventVersion()
{
    var order = new Order();
    var oldEvent = new OrderPlacedEventV1("ORD-001", 50m);
    
    order.ApplyHistoric(oldEvent, new StreamPosition(1));
    
    // Aggregate should still apply the event
    order.State.IsPlaced.Should().BeTrue();
    order.State.Total.Should().Be(50m);
}

[Fact]
public void ApplyEvent_HandlesNewEventVersion()
{
    var order = new Order();
    var newEvent = new OrderPlacedEventV2("ORD-001", 50m, "EUR");
    
    order.ApplyHistoric(newEvent, new StreamPosition(1));
    
    // Aggregate should apply the event with new data
    order.State.IsPlaced.Should().BeTrue();
    order.State.Total.Should().Be(50m);
}
```

## Best Practices

1. **Test One Thing**: Each test should verify a single behavior
2. **Clear Names**: Use test names that describe the scenario and expected outcome
3. **Arrange-Act-Assert**: Organize tests into clear phases
4. **No Test Interdependencies**: Tests should be runnable in any order
5. **Use Assertions**: Prefer FluentAssertions for readability
6. **Test Happy and Sad Paths**: Include both valid and invalid scenarios
7. **Avoid Test Fixtures for State**: Keep fixtures minimal and focused
8. **DequeueUncommitted is Key**: Use it to verify events raised by commands
9. **Test Event Ordering**: Ensure events are raised in the correct order
10. **Use Theories for Variations**: Use [Theory] and [InlineData] for multiple scenarios

## Summary

Testing aggregates in ZeroAlloc.EventSourcing focuses on:
- Verifying that commands raise the correct events
- Ensuring ApplyEvent is deterministic and pure
- Testing command validation and state transitions
- Using xUnit Facts and Theories effectively
- Following the arrange-act-assert pattern
- Testing both happy and sad paths
- Verifying idempotency where applicable

With these patterns, you can build comprehensive test suites that ensure your event-sourced domain logic is correct and maintainable.
