using System;
using System.Collections.Generic;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Examples.DomainModeling;

/// <summary>
/// This example shows a realistic Order aggregate demonstrating:
/// 1. Complex business logic with validation
/// 2. Multiple commands that raise events
/// 3. State machine pattern (order must follow a sequence)
/// 4. Collection handling (line items)
/// 5. Value objects within state
///
/// The example is split over three files, one concern each:
/// - OrderAggregate.cs (this file): identities, value objects and the aggregate's commands
/// - OrderState.cs: the state and how each event changes it
/// - OrderEvents.cs: the events
///
/// The files are compiled and run by the test suite, so they only use the public API.
/// </summary>

// Domain value types
public readonly record struct OrderId(Guid Value);
public readonly record struct CustomerId(Guid Value);
public readonly record struct ProductId(Guid Value);

// Value object: represents a line item
public record LineItem(ProductId ProductId, int Quantity, decimal UnitPrice)
{
    public decimal Total => Quantity * UnitPrice;
}

// Aggregate. The class is partial: the source generator adds the ApplyEvent override, which
// routes each event to the matching internal Apply method on OrderState, and an
// OrderEventTypeRegistry for the event store.
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    /// <summary>
    /// Place an order - initial command that creates the order.
    /// </summary>
    public void Place(string orderNumber, CustomerId customerId, IReadOnlyList<LineItem> lineItems)
    {
        // Validation: Order can only be placed once
        if (State.IsPlaced)
            throw new InvalidOperationException("Order already placed");

        // Validation: Must have at least one line item
        if (lineItems == null || lineItems.Count == 0)
            throw new ArgumentException("Order must have at least one line item");

        // Validation: Line items must be valid
        foreach (var item in lineItems)
        {
            if (item.Quantity <= 0)
                throw new ArgumentException("Item quantity must be positive");
            if (item.UnitPrice < 0)
                throw new ArgumentException("Item price cannot be negative");
        }

        // Validation: Order number format
        if (string.IsNullOrWhiteSpace(orderNumber))
            throw new ArgumentException("Order number required");

        // Business rule: Customer ID must be valid
        if (customerId.Value == Guid.Empty)
            throw new ArgumentException("Valid customer ID required");

        // All validations passed, raise event
        Raise(new OrderPlacedEvent(orderNumber, customerId, lineItems));
    }

    /// <summary>
    /// Confirm the order - indicates customer has confirmed the order.
    /// </summary>
    public void Confirm()
    {
        // State machine: Can only confirm placed orders
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm unplaced order");

        if (State.IsConfirmed)
            throw new InvalidOperationException("Order already confirmed");

        if (State.IsCancelled)
            throw new InvalidOperationException("Cannot confirm cancelled order");

        Raise(new OrderConfirmedEvent());
    }

    /// <summary>
    /// Process payment - indicates payment has been received.
    /// </summary>
    public void ProcessPayment(decimal amount)
    {
        // State machine: Payment only after confirmation
        if (!State.IsConfirmed)
            throw new InvalidOperationException("Cannot process payment before confirmation");

        if (State.IsPaid)
            throw new InvalidOperationException("Payment already processed");

        if (State.IsCancelled)
            throw new InvalidOperationException("Cannot process payment for cancelled order");

        // Business rule: Payment amount must match order total
        if (Math.Abs(amount - State.Total) > 0.01m)  // Allow 1 cent rounding difference
            throw new InvalidOperationException(
                $"Payment amount {amount} doesn't match order total {State.Total}");

        Raise(new PaymentProcessedEvent(amount));
    }

    /// <summary>
    /// Ship the order - indicates order has been shipped.
    /// </summary>
    public void Ship(string trackingNumber)
    {
        // State machine: Can only ship paid orders
        if (!State.IsPaid)
            throw new InvalidOperationException("Cannot ship unpaid order");

        if (State.IsShipped)
            throw new InvalidOperationException("Order already shipped");

        if (State.IsCancelled)
            throw new InvalidOperationException("Cannot ship cancelled order");

        // Business rule: Tracking number is required
        if (string.IsNullOrWhiteSpace(trackingNumber))
            throw new ArgumentException("Tracking number required for shipment");

        Raise(new OrderShippedEvent(trackingNumber));
    }

    /// <summary>
    /// Cancel the order - cancels the order before shipment.
    /// </summary>
    public void Cancel()
    {
        // Business rule: Can only cancel if not yet shipped
        if (State.IsShipped)
            throw new InvalidOperationException("Cannot cancel shipped order");

        if (State.IsCancelled)
            throw new InvalidOperationException("Order already cancelled");

        // If payment was processed, this would normally trigger a refund event
        // For simplicity, we just cancel here
        Raise(new OrderCancelledEvent());
    }
}
