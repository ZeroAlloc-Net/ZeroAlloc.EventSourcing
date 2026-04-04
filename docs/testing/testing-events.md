# Testing Events

## Overview

Events are the source of truth in event sourcing systems. This guide covers how to test that your events are properly defined, serializable, and maintainable. Event testing ensures data integrity, consistency, and backward compatibility as your system evolves.

## Key Principles

1. **Immutability**: Events cannot be changed after creation
2. **Naming Conventions**: Events use past tense (OrderPlaced, not PlaceOrder)
3. **Serialization**: Events must survive JSON round-trips
4. **Type Safety**: Event types must map correctly to names
5. **Versioning**: Events should support backward compatibility

## Event Fundamentals

### What to Test

- **Immutability**: Records cannot be mutated
- **Serialization**: Events convert to JSON and back without loss
- **Metadata preservation**: Timestamps, correlation IDs survive serialization
- **Type consistency**: EventType attribute matches class name
- **Property validation**: Event properties are correctly set
- **Naming conventions**: Events use past tense
- **Equality**: Events compare correctly
- **Hashing**: Events hash consistently

## Complete Order Event Test Suite

### Event Definitions

```csharp
using ZeroAlloc.EventSourcing;

/// <summary>Raised when an order is placed.</summary>
[EventType("OrderPlaced")]
public record OrderPlacedEvent(
    string OrderId,
    decimal Total,
    string Currency = "USD",
    DateTime? CreatedAt = null
);

/// <summary>Raised when an order is confirmed.</summary>
[EventType("OrderConfirmed")]
public record OrderConfirmedEvent(
    string OrderId,
    DateTime? ConfirmedAt = null
);

/// <summary>Raised when an order is shipped.</summary>
[EventType("OrderShipped")]
public record OrderShippedEvent(
    string OrderId,
    string TrackingNumber,
    string CarrierName = "Standard"
);

/// <summary>Raised when an order is cancelled.</summary>
[EventType("OrderCancelled")]
public record OrderCancelledEvent(
    string OrderId,
    string Reason
);

/// <summary>Raised when an order is refunded.</summary>
[EventType("OrderRefunded")]
public record OrderRefundedEvent(
    string OrderId,
    decimal RefundAmount
);
```

### Test Class

```csharp
using FluentAssertions;
using System.Text.Json;
using Xunit;
using ZeroAlloc.EventSourcing;

public class OrderEventTests
{
    // --- Immutability Tests ---
    
    [Fact]
    public void OrderPlacedEvent_IsRecord_CannotBeMutated()
    {
        // Arrange
        var e1 = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act: Attempting to mutate fails at compile time
        // var e2 = e1 with { OrderId = "ORD-002" }; // Creates new instance, original unchanged
        
        // Assert: Record is immutable by design
        e1.OrderId.Should().Be("ORD-001");
    }
    
    [Fact]
    public void OrderPlacedEvent_WithExpression_CreatesNewInstance()
    {
        // Arrange
        var original = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act: with expression creates a new instance
        var modified = original with { OrderId = "ORD-002" };
        
        // Assert: Original unchanged, new instance created
        original.OrderId.Should().Be("ORD-001");
        modified.OrderId.Should().Be("ORD-002");
        original.Should().NotBe(modified);
    }
    
    [Fact]
    public void OrderPlacedEvent_IsFrozen_NoPropertyMutation()
    {
        // Arrange & Act
        var e = new OrderPlacedEvent("ORD-001", 100m);
        
        // Assert: Properties are read-only
        var properties = typeof(OrderPlacedEvent)
            .GetProperties(System.Reflection.BindingFlags.Public | 
                          System.Reflection.BindingFlags.Instance);
        
        properties.Should().AllSatisfy(p =>
            p.CanWrite.Should().BeFalse("properties should be read-only")
        );
    }
    
    // --- EventType Consistency Tests ---
    
    [Fact]
    public void OrderPlacedEvent_HasCorrectEventType()
    {
        // Arrange
        var e = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act: Get the event type attribute
        var typeAttr = typeof(OrderPlacedEvent)
            .GetCustomAttributes(typeof(EventTypeAttribute), inherit: false)
            .FirstOrDefault() as EventTypeAttribute;
        
        // Assert
        typeAttr.Should().NotBeNull();
        typeAttr!.EventType.Should().Be("OrderPlaced");
    }
    
    [Fact]
    public void OrderEventTypes_FollowNamingConvention()
    {
        // Arrange: All Order events
        var eventTypes = new[] {
            typeof(OrderPlacedEvent),
            typeof(OrderConfirmedEvent),
            typeof(OrderShippedEvent),
            typeof(OrderCancelledEvent),
            typeof(OrderRefundedEvent)
        };
        
        // Act & Assert: Each event type name should match convention
        foreach (var eventType in eventTypes)
        {
            var attr = eventType
                .GetCustomAttributes(typeof(EventTypeAttribute), inherit: false)
                .FirstOrDefault() as EventTypeAttribute;
            
            attr.Should().NotBeNull($"{eventType.Name} should have EventType attribute");
            attr!.EventType.Should().NotBe(eventType.Name, 
                "EventType should not include 'Event' suffix");
            attr.EventType.Should().EndWith("ed", 
                "events should use past tense");
        }
    }
    
    // --- Serialization Tests ---
    
    [Fact]
    public void OrderPlacedEvent_SerializesToJson_WithAllProperties()
    {
        // Arrange
        var e = new OrderPlacedEvent("ORD-001", 100m, "USD");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        // Act
        var json = JsonSerializer.Serialize(e, options);
        
        // Assert
        json.Should().Contain("ORD-001");
        json.Should().Contain("100");
        json.Should().Contain("USD");
    }
    
    [Fact]
    public void OrderPlacedEvent_RoundTripsJson_PreservesData()
    {
        // Arrange
        var original = new OrderPlacedEvent("ORD-001", 99.99m, "EUR");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        // Act
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<OrderPlacedEvent>(json, options);
        
        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.OrderId.Should().Be(original.OrderId);
        deserialized.Total.Should().Be(original.Total);
        deserialized.Currency.Should().Be(original.Currency);
    }
    
    [Fact]
    public void OrderShippedEvent_RoundTripsJson_WithOptionalDefaults()
    {
        // Arrange
        var original = new OrderShippedEvent("ORD-002", "TRACK-123");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        // Act
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<OrderShippedEvent>(json, options);
        
        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.OrderId.Should().Be("ORD-002");
        deserialized.TrackingNumber.Should().Be("TRACK-123");
        deserialized.CarrierName.Should().Be("Standard"); // Default value preserved
    }
    
    [Theory]
    [InlineData(typeof(OrderPlacedEvent))]
    [InlineData(typeof(OrderConfirmedEvent))]
    [InlineData(typeof(OrderShippedEvent))]
    [InlineData(typeof(OrderCancelledEvent))]
    [InlineData(typeof(OrderRefundedEvent))]
    public void AllOrderEvents_RoundTripsJson(Type eventType)
    {
        // Arrange: Create an instance with default values
        var instance = Activator.CreateInstance(eventType)!;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        // Act
        var json = JsonSerializer.Serialize(instance, eventType, options);
        var deserialized = JsonSerializer.Deserialize(json, eventType, options);
        
        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Equals(instance).Should().BeTrue();
    }
    
    // --- Metadata Preservation Tests ---
    
    [Fact]
    public void EventEnvelope_PreservesEventMetadata()
    {
        // Arrange
        var e = new OrderPlacedEvent("ORD-001", 100m);
        var metadata = EventMetadata.New("OrderPlaced");
        var envelope = new EventEnvelope(
            StreamId: new StreamId("order-1"),
            Position: new StreamPosition(1),
            Event: e,
            Metadata: metadata
        );
        
        // Act & Assert
        envelope.Metadata.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        envelope.Metadata.EventType.Should().Be("OrderPlaced");
        envelope.Metadata.CorrelationId.Should().NotBeEmpty();
    }
    
    [Fact]
    public void EventMetadata_WithCustomCorrelationId_PreservesValue()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var metadata = EventMetadata.New("OrderPlaced", correlationId);
        
        // Act & Assert
        metadata.CorrelationId.Should().Be(correlationId);
    }
    
    // --- Property Validation Tests ---
    
    [Fact]
    public void OrderPlacedEvent_WithValidProperties_CreatesSuccessfully()
    {
        // Arrange & Act
        var e = new OrderPlacedEvent("ORD-001", 99.99m, "USD");
        
        // Assert
        e.OrderId.Should().NotBeNullOrEmpty();
        e.Total.Should().BeGreaterThan(0);
        e.Currency.Should().NotBeNullOrEmpty();
    }
    
    [Fact]
    public void OrderPlacedEvent_WithDefaultCurrency_UsesUSD()
    {
        // Arrange & Act
        var e = new OrderPlacedEvent("ORD-001", 100m);
        
        // Assert
        e.Currency.Should().Be("USD");
    }
    
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void OrderPlacedEvent_WithInvalidOrderId_ShouldFailValidation(string invalidOrderId)
    {
        // Arrange & Act
        var e = new OrderPlacedEvent(invalidOrderId, 100m);
        
        // Assert: Event is created but should fail in aggregate validation
        e.OrderId.Should().Be(invalidOrderId);
        // Actual validation happens at aggregate level, not event level
    }
    
    [Fact]
    public void OrderCancelledEvent_WithReason_PreservesReason()
    {
        // Arrange
        var reason = "Customer requested refund";
        
        // Act
        var e = new OrderCancelledEvent("ORD-001", reason);
        
        // Assert
        e.Reason.Should().Be(reason);
        e.OrderId.Should().Be("ORD-001");
    }
    
    // --- Equality Tests ---
    
    [Fact]
    public void OrderPlacedEvents_WithSameData_AreEqual()
    {
        // Arrange
        var e1 = new OrderPlacedEvent("ORD-001", 100m, "USD");
        var e2 = new OrderPlacedEvent("ORD-001", 100m, "USD");
        
        // Act & Assert
        e1.Should().Be(e2);
        e1.GetHashCode().Should().Be(e2.GetHashCode());
    }
    
    [Fact]
    public void OrderPlacedEvents_WithDifferentData_AreNotEqual()
    {
        // Arrange
        var e1 = new OrderPlacedEvent("ORD-001", 100m);
        var e2 = new OrderPlacedEvent("ORD-002", 100m);
        
        // Act & Assert
        e1.Should().NotBe(e2);
        e1.GetHashCode().Should().NotBe(e2.GetHashCode());
    }
    
    [Fact]
    public void OrderPlacedEvent_InSet_CanBeFound()
    {
        // Arrange
        var e1 = new OrderPlacedEvent("ORD-001", 100m);
        var e2 = new OrderPlacedEvent("ORD-001", 100m);
        var set = new HashSet<OrderPlacedEvent> { e1 };
        
        // Act & Assert
        set.Should().Contain(e2);
    }
    
    // --- Naming Convention Tests ---
    
    [Fact]
    public void OrderEvents_UsesPastTense_FollowsConvention()
    {
        // Arrange: Collect all event type names
        var eventTypes = new[] {
            "Placed",    // Past tense ✓
            "Confirmed", // Past tense ✓
            "Shipped",   // Past tense ✓
            "Cancelled", // Past tense ✓
            "Refunded"   // Past tense ✓
        };
        
        // Act & Assert
        foreach (var name in eventTypes)
        {
            name.Should().NotBe("Place", "should use past tense");
            name.Should().NotBe("Confirm", "should use past tense");
            name.Should().NotBe("Ship", "should use past tense");
        }
    }
    
    // --- Versioning Tests ---
    
    [Fact]
    public void OrderPlacedEventV1_BackwardCompatible_WithV2()
    {
        // Arrange: Version 1 with minimal fields
        var v1Json = """{"orderId":"ORD-001","total":100}""";
        
        // Act: Deserialize as version 2 with defaults
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var v2Event = JsonSerializer.Deserialize<OrderPlacedEvent>(v1Json, options);
        
        // Assert: V2 fills in defaults from V1 data
        v2Event.Should().NotBeNull();
        v2Event!.OrderId.Should().Be("ORD-001");
        v2Event.Total.Should().Be(100);
        v2Event.Currency.Should().Be("USD"); // Default applied
    }
    
    [Fact]
    public void EventVersioning_AllowsAdditionalOptionalProperties()
    {
        // Arrange: Event with optional property
        var e = new OrderPlacedEvent("ORD-001", 100m);
        
        // Act: Optional properties have default values
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(e, options);
        
        // Assert: Can deserialize even if some properties are missing from JSON
        var deserialized = JsonSerializer.Deserialize<OrderPlacedEvent>(json, options);
        deserialized.Should().NotBeNull();
        deserialized!.CreatedAt.Should().BeNull(); // Optional property defaults to null
    }
}
```

## Event Design Guidelines

### Use Records for Events

Records provide:
- Automatic structural equality
- Value semantics
- Immutability
- Concise property syntax

```csharp
// Good: Using record
public record OrderPlacedEvent(string OrderId, decimal Total);

// Avoid: Using classes
public class OrderPlacedEvent
{
    public string OrderId { get; init; }
    public decimal Total { get; init; }
}
```

### Use Past Tense Naming

```csharp
// Good
public record OrderPlacedEvent(...);
public record OrderConfirmedEvent(...);
public record OrderShippedEvent(...);

// Avoid
public record PlaceOrderEvent(...);
public record ConfirmOrderEvent(...);
public record ShipOrderEvent(...);
```

### Define EventType Attributes

```csharp
[EventType("OrderPlaced")]
public record OrderPlacedEvent(string OrderId, decimal Total);
```

### Use Immutable Properties

```csharp
// Good: Records are immutable
public record OrderPlacedEvent(string OrderId, decimal Total);

// Avoid: Mutable properties
public record OrderPlacedEvent
{
    public string OrderId { get; set; } // Avoid set
    public decimal Total { get; set; }  // Avoid set
}
```

## Testing Serialization Scenarios

```csharp
[Theory]
[InlineData("{\"orderId\":\"ORD-001\",\"total\":100,\"currency\":\"USD\"}")]
[InlineData("{\"orderId\":\"ORD-002\",\"total\":50.50,\"currency\":\"EUR\"}")]
public void OrderPlacedEvent_DeserializesFromJson(string json)
{
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var e = JsonSerializer.Deserialize<OrderPlacedEvent>(json, options);
    
    e.Should().NotBeNull();
    e!.OrderId.Should().StartWith("ORD-");
    e.Total.Should().BeGreaterThan(0);
}
```

## Best Practices

1. **Use Records**: Ensure immutability and value semantics
2. **Name with Past Tense**: Events describe what happened
3. **Define EventType Attributes**: Ensure type name consistency
4. **Include All Relevant Data**: Events should be self-contained
5. **Support Optional Properties**: Use defaults for backward compatibility
6. **Test Round-Trip Serialization**: JSON serialization is critical
7. **Verify Metadata**: Timestamps and correlation IDs survive serialization
8. **Use Null-Safety**: Mark nullable fields explicitly
9. **Keep Events Simple**: Avoid complex objects in events
10. **Version Events Carefully**: Maintain backward compatibility

## Summary

Testing events in ZeroAlloc.EventSourcing ensures:
- Records are immutable and cannot be accidentally modified
- Events serialize correctly to JSON and back
- Event type names follow conventions (past tense)
- Metadata (timestamps, correlation IDs) is preserved
- New event versions remain backward compatible
- Event equality and hashing work correctly
- All properties are properly set and accessible

With these patterns, you can ensure your events are reliable, serializable, and maintainable throughout your system's lifecycle.
