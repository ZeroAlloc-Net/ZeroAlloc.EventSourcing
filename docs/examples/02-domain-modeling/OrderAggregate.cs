using System;
using System.Collections.Generic;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.DomainModeling;

/// <summary>
/// This example shows a realistic Order aggregate demonstrating:
/// 1. Complex business logic with validation
/// 2. Multiple commands that raise events
/// 3. State machine pattern (order must follow a sequence)
/// 4. Collection handling (line items)
/// 5. Value objects within state
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

// Aggregate state
public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;

    // Identification
    public CustomerId CustomerId { get; private set; }
    public string OrderNumber { get; private set; }

    // Status flags (state machine)
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsPaid { get; private set; }
    public bool IsShipped { get; private set; }
    public bool IsCancelled { get; private set; }

    // Content
    public List<LineItem> LineItems { get; private set; }
    public decimal Total => CalculateTotal();

    // Audit trail
    public DateTime PlacedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? ShippedAt { get; private set; }

    // Shipping
    public string? TrackingNumber { get; private set; }

    private decimal CalculateTotal() => LineItems?.Sum(l => l.Total) ?? 0;

    // Event application methods
    internal OrderState Apply(OrderPlacedEvent e)
    {
        var newState = this with
        {
            OrderNumber = e.OrderNumber,
            CustomerId = e.CustomerId,
            IsPlaced = true,
            PlacedAt = DateTime.UtcNow,
            LineItems = new List<LineItem>(e.LineItems)
        };
        return newState;
    }

    internal OrderState Apply(OrderConfirmedEvent _) =>
        this with
        {
            IsConfirmed = true,
            ConfirmedAt = DateTime.UtcNow
        };

    internal OrderState Apply(PaymentProcessedEvent _) =>
        this with { IsPaid = true };

    internal OrderState Apply(OrderShippedEvent e) =>
        this with
        {
            IsShipped = true,
            ShippedAt = DateTime.UtcNow,
            TrackingNumber = e.TrackingNumber
        };

    internal OrderState Apply(OrderCancelledEvent _) =>
        this with { IsCancelled = true };
}

// Events
public record OrderPlacedEvent(
    string OrderNumber,
    CustomerId CustomerId,
    IReadOnlyList<LineItem> LineItems);

public record OrderConfirmedEvent;
public record PaymentProcessedEvent(decimal Amount);
public record OrderShippedEvent(string TrackingNumber);
public record OrderCancelledEvent;

// Aggregate
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

    // Event dispatch
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderConfirmedEvent e => state.Apply(e),
        PaymentProcessedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        OrderCancelledEvent e => state.Apply(e),
        _ => state
    };
}
