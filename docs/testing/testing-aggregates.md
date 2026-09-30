# Testing Aggregates

## Overview

Testing aggregates in ZeroAlloc.EventSourcing is about verifying that your domain entities correctly raise events and apply state transitions. This guide covers unit testing strategies for aggregates using xUnit and FluentAssertions.

## Key Principles

1. **Arrange-Act-Assert**: Structure tests with clear setup, action, and verification phases
2. **Test Events, Not Just State**: Verify the events raised, not just the final state
3. **Purity**: The state's `Apply` methods must be deterministic and side-effect free
4. **State Verification**: Ensure final aggregate state matches expectations
5. **Command Validation**: Test both happy paths and sad paths (validation failures)

## Aggregate Testing Fundamentals

### What to Test

- **Command processing**: Does the aggregate correctly handle commands?
- **Event raising**: Are the right events raised for a given command?
- **State application**: Do the state's `Apply` methods correctly update state?
- **Validation**: Are invalid inputs rejected appropriately?
- **State transitions**: Does the aggregate enforce valid state flows?

### Testing Pattern

Tests use only the public API of the aggregate: its command methods, `State`, `Version` and
`OriginalVersion`. The queue of uncommitted events is internal; the repository drains it when it
saves. To see which events a command raised, save the aggregate through an
`AggregateRepository` over the in-memory event store and read the stream back. The
[`OrderTestHarness`](#test-harness) below does exactly that.

```csharp
[Fact]
public async Task CommandName_Condition_ExpectedBehavior()
{
    // Arrange: Set up the aggregate and a repository over an in-memory store
    var harness = new OrderTestHarness();
    using var order = new Order();
    
    // Act: Execute the command
    order.PlaceOrder("ORD-001", 99.99m);
    
    // Assert: Verify the results
    var events = await harness.SaveAndReadNewEventsAsync(order);
    events.Should().ContainSingle().Which.Should().BeOfType<OrderPlacedEvent>();
    order.State.IsPlaced.Should().BeTrue();
}
```

A test that only checks state or validation does not need the harness at all. `Version` counts
every event applied to the aggregate, raised or loaded, and `OriginalVersion` is the version it
was loaded or last saved at. So `order.Version.Value - order.OriginalVersion.Value` is the number
of events waiting to be saved, and `order.Version == order.OriginalVersion` means none are.

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
        if (!State.IsPlaced)
            throw new InvalidOperationException("Order must be placed before it can be cancelled");
        
        if (State.IsCancelled)
            throw new InvalidOperationException("Order is already cancelled");
        
        if (State.IsShipped)
            throw new InvalidOperationException("Cannot cancel a shipped order");
        
        Raise(new OrderCancelledEvent(reason));
    }
}
```

`Order` is `partial` and has no hand-written `ApplyEvent`: the source generator emits the
`ApplyEvent` switch from the state's internal `Apply` methods, plus an `OrderEventTypeRegistry`
that the event store uses to read the events back.

### Test Harness

The library ships no test helpers, and you need none beyond a repository over the in-memory
event store. Create one harness per test so tests share no state.

```csharp
public sealed class OrderTestHarness
{
    public OrderTestHarness()
    {
        EventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),          // any IEventSerializer
            new OrderEventTypeRegistry());      // source-generated for Order
        Repository = new AggregateRepository<Order, OrderId>(
            EventStore, () => new Order(), StreamIdFor);
    }

    public IEventStore EventStore { get; }
    public IAggregateRepository<Order, OrderId> Repository { get; }
    public OrderId OrderId { get; } = new(Guid.NewGuid());

    public static StreamId StreamIdFor(OrderId id) => new($"order-{id.Value}");

    /// <summary>Saves the order and returns the events that save appended, in order.</summary>
    public async Task<List<object>> SaveAndReadNewEventsAsync(Order order)
    {
        var from = order.OriginalVersion;
        var saved = await Repository.SaveAsync(order, OrderId);
        saved.IsSuccess.Should().BeTrue();

        // A read yields the events after `from`, so these are only the ones this save appended
        var events = new List<object>();
        await foreach (var envelope in EventStore.ReadAsync(StreamIdFor(OrderId), from))
            events.Add(envelope.Event);
        return events;
    }

    /// <summary>"Given these events happened": appends them, then loads the order.</summary>
    public async Task<Order> GivenAsync(params object[] history)
    {
        var appended = await EventStore.AppendAsync(StreamIdFor(OrderId), history, StreamPosition.Start);
        appended.IsSuccess.Should().BeTrue();

        var loaded = await Repository.LoadAsync(OrderId);
        return loaded.Value;
    }
}
```

A complete version of this harness, compiled and run by the library's own test suite, is in
[`docs/examples/03-testing/TestingAggregates.cs`](../examples/03-testing/TestingAggregates.cs).

### Test Class

```csharp
public class OrderAggregateTests
{
    // --- Initialization Tests ---
    
    [Fact]
    public void NewOrder_HasInitialState()
    {
        // Arrange & Act
        using var order = new Order();
        
        // Assert
        order.Version.Should().Be(StreamPosition.Start);
        order.OriginalVersion.Should().Be(StreamPosition.Start);
        order.State.IsPlaced.Should().BeFalse();
        order.State.IsConfirmed.Should().BeFalse();
        order.State.IsShipped.Should().BeFalse();
        order.State.Total.Should().Be(0m);
    }
    
    // --- Happy Path Tests ---
    
    [Fact]
    public async Task PlaceOrder_WithValidInput_RaisesOrderPlacedEvent()
    {
        // Arrange
        var harness = new OrderTestHarness();
        using var order = new Order();
        var orderId = "ORD-001";
        var total = 99.99m;
        
        // Act
        order.PlaceOrder(orderId, total);
        
        // Assert
        var events = await harness.SaveAndReadNewEventsAsync(order);
        var e = events.Should().ContainSingle().Which.Should().BeOfType<OrderPlacedEvent>().Subject;
        e.OrderId.Should().Be(orderId);
        e.Total.Should().Be(total);
        
        order.State.IsPlaced.Should().BeTrue();
        order.State.Total.Should().Be(total);
    }
    
    [Fact]
    public async Task ConfirmOrder_AfterPlaced_RaisesOrderConfirmedEvent()
    {
        // Arrange: the order was placed earlier
        var harness = new OrderTestHarness();
        using var order = await harness.GivenAsync(new OrderPlacedEvent("ORD-001", 50m));
        
        // Act
        order.ConfirmOrder();
        
        // Assert
        var events = await harness.SaveAndReadNewEventsAsync(order);
        events.Should().ContainSingle().Which.Should().BeOfType<OrderConfirmedEvent>();
        
        order.State.IsConfirmed.Should().BeTrue();
    }
    
    [Fact]
    public async Task ShipOrder_WithValidTracking_RaisesOrderShippedEvent()
    {
        // Arrange
        var harness = new OrderTestHarness();
        using var order = await harness.GivenAsync(
            new OrderPlacedEvent("ORD-001", 50m),
            new OrderConfirmedEvent());
        
        var tracking = "TRACK-123456";
        
        // Act
        order.ShipOrder(tracking);
        
        // Assert
        var events = await harness.SaveAndReadNewEventsAsync(order);
        var e = events.Should().ContainSingle().Which.Should().BeOfType<OrderShippedEvent>().Subject;
        e.TrackingNumber.Should().Be(tracking);
        
        order.State.IsShipped.Should().BeTrue();
        order.State.TrackingNumber.Should().Be(tracking);
    }
    
    [Fact]
    public async Task CompleteOrderFlow_PlaceConfirmShip_AllEventsRaised()
    {
        // Arrange
        var harness = new OrderTestHarness();
        using var order = new Order();
        
        // Act
        order.PlaceOrder("ORD-001", 100m);
        order.ConfirmOrder();
        order.ShipOrder("TRACK-789");
        
        // Assert
        var events = await harness.SaveAndReadNewEventsAsync(order);
        events.Should().HaveCount(3);
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
        using var order = new Order();
        
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            order.PlaceOrder("ORD-001", invalidTotal)
        );
        
        exception.Message.Should().Contain("positive");
        order.State.IsPlaced.Should().BeFalse();
        order.Version.Should().Be(StreamPosition.Start); // no event was raised
    }
    
    [Fact]
    public void ConfirmOrder_WhenNotPlaced_ThrowsInvalidOperationException()
    {
        // Arrange
        using var order = new Order();
        
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
        using var order = new Order();
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
        using var order = new Order();
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
    public void ShipOrder_WithInvalidTracking_ThrowsArgumentException(string? invalidTracking)
    {
        // Arrange
        using var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.ConfirmOrder();
        
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            order.ShipOrder(invalidTracking!)
        );
        
        exception.Message.Should().Contain("Tracking");
        order.State.IsShipped.Should().BeFalse();
    }
    
    // --- Edge Cases ---
    
    [Fact]
    public void CancelOrder_BeforePlaced_ThrowsInvalidOperationException()
    {
        // Arrange
        using var order = new Order();
        
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
        using var order = new Order();
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
        using var order = new Order();
        order.PlaceOrder("ORD-001", 50m);
        order.Cancel("Changed mind");
        
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            order.Cancel("Duplicate cancel")
        );
        
        exception.Message.Should().Contain("already cancelled");
    }
    
    [Fact]
    public async Task CancelOrder_BeforeShipping_Succeeds()
    {
        // Arrange
        var harness = new OrderTestHarness();
        using var order = await harness.GivenAsync(
            new OrderPlacedEvent("ORD-001", 50m),
            new OrderConfirmedEvent());
        
        // Act
        order.Cancel("Customer requested");
        
        // Assert
        var events = await harness.SaveAndReadNewEventsAsync(order);
        events.Should().ContainSingle().Which.Should().BeOfType<OrderCancelledEvent>();
        
        order.State.IsCancelled.Should().BeTrue();
    }
    
    // --- Replay Tests ---
    
    [Fact]
    public async Task LoadedEvents_AreNotSavedAgain()
    {
        // Arrange & Act: load an order whose history is already in the store
        var harness = new OrderTestHarness();
        using var order = await harness.GivenAsync(new OrderPlacedEvent("ORD-001", 99.99m));
        
        // Assert: replayed events update state and version, but nothing is pending
        order.State.IsPlaced.Should().BeTrue();
        order.Version.Value.Should().Be(1);
        order.OriginalVersion.Should().Be(order.Version);
        (await harness.SaveAndReadNewEventsAsync(order)).Should().BeEmpty();
    }
    
    [Fact]
    public async Task Replay_IsDeterministic_SameHistoryProducesSameState()
    {
        // Arrange
        var placedEvent = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act: the same history, replayed onto two fresh aggregates
        using var order1 = await new OrderTestHarness().GivenAsync(placedEvent);
        using var order2 = await new OrderTestHarness().GivenAsync(placedEvent);
        
        // Assert
        order1.State.Should().Be(order2.State);
    }
    
    [Fact]
    public async Task UnknownEventType_IsSkippedOnLoad()
    {
        // Arrange & Act: a stream that also holds an event type Order does not handle
        var harness = new OrderTestHarness();
        using var order = await harness.GivenAsync(
            new OrderPlacedEvent("ORD-001", 50m),
            new SomeOtherContextEvent("not an order event"));
        
        // Assert: the event store skips types its registry cannot resolve,
        // for forward compatibility, so state reflects only the known events
        order.State.IsPlaced.Should().BeTrue();
        order.State.IsConfirmed.Should().BeFalse();
    }
    
    // --- Save Tests ---
    
    [Fact]
    public async Task Save_AppendsOnlyEventsRaisedSinceTheLastSave()
    {
        // Arrange
        var harness = new OrderTestHarness();
        using var order = new Order();
        
        // Act
        order.PlaceOrder("ORD-001", 100m);
        order.ConfirmOrder();
        var firstSave = await harness.SaveAndReadNewEventsAsync(order);
        
        order.ShipOrder("TRACK-123");
        var secondSave = await harness.SaveAndReadNewEventsAsync(order);
        var thirdSave = await harness.SaveAndReadNewEventsAsync(order);
        
        // Assert
        firstSave.Should().HaveCount(2);
        secondSave.Should().ContainSingle().Which.Should().BeOfType<OrderShippedEvent>();
        thirdSave.Should().BeEmpty();
    }
}

// Not an Order event: OrderState has no Apply for it, so OrderEventTypeRegistry does not know it
public record SomeOtherContextEvent(string Note);
```

## xUnit Testing Patterns

### Using Facts vs Theories

```csharp
// Fact: Single scenario with fixed values
[Fact]
public void PlaceOrder_ValidInput_Succeeds()
{
    using var order = new Order();
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
    using var order = new Order();
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
        using var order = _fixture.CreatePlacedOrder(75m);
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
    using var order = new Order();
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
    using var order = new Order();
    Assert.Throws<InvalidOperationException>(() => order.ConfirmOrder());
    
    // Cannot ship without confirming
    using var order2 = new Order();
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
    using var order = new Order();
    var ex = Assert.Throws<ArgumentException>(() =>
        order.PlaceOrder("ORD-001", invalidTotal)
    );
    
    ex.Message.Should().Contain("positive");
    order.State.IsPlaced.Should().BeFalse();
    order.Version.Should().Be(StreamPosition.Start); // no event was raised
}

[Theory]
[InlineData("")]
[InlineData(null)]
[InlineData("   ")]
public void ShipOrder_WithInvalidTracking_Throws(string? invalidTracking)
{
    using var order = new Order();
    order.PlaceOrder("ORD-001", 50m);
    order.ConfirmOrder();
    
    var ex = Assert.Throws<ArgumentException>(() =>
        order.ShipOrder(invalidTracking!)
    );
    
    ex.Message.Should().Contain("Tracking");
}
```

## Testing Event Versioning

When events evolve, old versions stay in the stream, so the state keeps an internal `Apply` for
each version it can meet. Test that both versions load, by appending them with the harness:

```csharp
// Old event version, still in existing streams
public record OrderPlacedEventV1(string OrderId, decimal Total);

// New event version with additional field
public record OrderPlacedEventV2(
    string OrderId, 
    decimal Total, 
    string Currency = "USD"
);

// In OrderState: one Apply per version
//   internal OrderState Apply(OrderPlacedEventV1 e) => this with { IsPlaced = true, Total = e.Total };
//   internal OrderState Apply(OrderPlacedEventV2 e) => this with { IsPlaced = true, Total = e.Total };

[Fact]
public async Task Load_HandlesOldEventVersion()
{
    var harness = new OrderTestHarness();
    
    using var order = await harness.GivenAsync(new OrderPlacedEventV1("ORD-001", 50m));
    
    // Aggregate should still apply the event
    order.State.IsPlaced.Should().BeTrue();
    order.State.Total.Should().Be(50m);
}

[Fact]
public async Task Load_HandlesNewEventVersion()
{
    var harness = new OrderTestHarness();
    
    using var order = await harness.GivenAsync(new OrderPlacedEventV2("ORD-001", 50m, "EUR"));
    
    // Aggregate should apply the event with new data
    order.State.IsPlaced.Should().BeTrue();
    order.State.Total.Should().Be(50m);
}
```

To convert old events to the new shape on read instead, register an upcaster; see
[Event Versioning and Evolution](../core-concepts/events.md#event-versioning-and-evolution).

## Best Practices

1. **Test One Thing**: Each test should verify a single behavior
2. **Clear Names**: Use test names that describe the scenario and expected outcome
3. **Arrange-Act-Assert**: Organize tests into clear phases
4. **No Test Interdependencies**: Tests should be runnable in any order; create a harness per test
5. **Use Assertions**: Prefer FluentAssertions for readability
6. **Test Happy and Sad Paths**: Include both valid and invalid scenarios
7. **Avoid Test Fixtures for State**: Keep fixtures minimal and focused
8. **Save and Read Back**: Save through a repository over the in-memory store to verify the events a command raised
9. **Test Event Ordering**: Ensure events are raised in the correct order
10. **Use Theories for Variations**: Use [Theory] and [InlineData] for multiple scenarios

## Summary

Testing aggregates in ZeroAlloc.EventSourcing focuses on:
- Verifying that commands raise the correct events
- Ensuring state transitions are deterministic and pure
- Testing command validation and state transitions
- Using xUnit Facts and Theories effectively
- Following the arrange-act-assert pattern
- Testing both happy and sad paths
- Verifying that loaded events are not saved again

With these patterns, you can build comprehensive test suites that ensure your event-sourced domain logic is correct and maintainable.
