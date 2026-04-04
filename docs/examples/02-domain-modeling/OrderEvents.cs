using System;
using System.Collections.Generic;

namespace ZeroAlloc.EventSourcing.Examples.DomainModeling;

/// <summary>
/// This file contains all events related to the Order aggregate.
///
/// Design principles:
/// 1. Events are named in past tense (OrderPlaced, not PlaceOrder)
/// 2. Events are immutable records
/// 3. Events contain all relevant information about what happened
/// 4. Events include both IDs and denormalized data for context
/// </summary>

// Event 1: Order was placed
// Contains all information needed to reconstitute the order state
public record OrderPlacedEvent(
    string OrderNumber,
    CustomerId CustomerId,
    IReadOnlyList<LineItem> LineItems)
{
    /// <summary>Example of enrichment - timestamp would typically come from EventEnvelope</summary>
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    public override string ToString() =>
        $"OrderPlaced: #{OrderNumber} for customer {CustomerId.Value} with {LineItems.Count} items, total ${LineItems.Sum(l => l.Total):F2}";
}

// Event 2: Order was confirmed by customer
// A thin event - just signals that confirmation happened
public record OrderConfirmedEvent
{
    public override string ToString() => "OrderConfirmed";
}

// Event 3: Payment was processed
// Captures the amount paid (useful for partial payments, refunds, etc.)
public record PaymentProcessedEvent(decimal Amount)
{
    public override string ToString() => $"PaymentProcessed: ${Amount:F2}";
}

// Event 4: Order was shipped
// Captures shipping information
public record OrderShippedEvent(string TrackingNumber)
{
    public override string ToString() => $"OrderShipped: tracking {TrackingNumber}";
}

// Event 5: Order was cancelled
// Could include reason in real system
public record OrderCancelledEvent
{
    public override string ToString() => "OrderCancelled";
}

// Alternative event versioning example:
// As business requirements evolve, you might need to add information to events.
// Instead of modifying existing events, create new versions:

/// <summary>
/// Version 2 of OrderPlaced with additional customer information.
/// Old orders will still use OrderPlacedEvent.
/// New orders will use OrderPlacedEventV2.
/// Both are handled in the aggregate's ApplyEvent method.
/// </summary>
public record OrderPlacedEventV2(
    string OrderNumber,
    CustomerId CustomerId,
    IReadOnlyList<LineItem> LineItems,
    string CustomerEmail,  // NEW in V2
    string CustomerName)   // NEW in V2
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    public override string ToString() =>
        $"OrderPlaced v2: #{OrderNumber} for {CustomerName} ({CustomerEmail})";
}

// Example: Refund event (for cancellation with payment)
public record RefundInitiatedEvent(decimal Amount)
{
    public string Reason { get; init; } = "Customer request";

    public override string ToString() => $"RefundInitiated: ${Amount:F2} - {Reason}";
}

// Example: Shipment event with more details
public record OrderShippedEventV2(
    string TrackingNumber,
    string ShippingCarrier,  // NEW
    DateTime EstimatedDelivery)  // NEW
{
    public override string ToString() =>
        $"OrderShipped v2: {ShippingCarrier} tracking {TrackingNumber}, arrives {EstimatedDelivery:yyyy-MM-dd}";
}

// Example: Order modification (add item)
// This demonstrates commands that can be executed after order is placed
public record LineItemAddedEvent(ProductId ProductId, int Quantity, decimal UnitPrice)
{
    public override string ToString() =>
        $"LineItemAdded: product {ProductId.Value}, qty {Quantity}, ${UnitPrice:F2} each";
}

// Example: Order modification (remove item)
public record LineItemRemovedEvent(ProductId ProductId)
{
    public override string ToString() => $"LineItemRemoved: product {ProductId.Value}";
}
