using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.DomainModeling;

/// <summary>
/// This file focuses on the Order aggregate state and demonstrates:
/// 1. Struct-based immutable state
/// 2. Pure function event application
/// 3. Derived properties (calculated from state)
/// 4. State validation invariants
///
/// The state struct is the "permanent record" of an order's current condition.
/// It's reconstructed by replaying events, never mutated directly.
/// </summary>

/// <summary>
/// Extended OrderState with more features.
/// This represents everything we know about an order at a point in time.
/// </summary>
public partial struct OrderState : IAggregateState<OrderState>
{
    // ===== Identity =====

    /// <summary>The customer who placed this order.</summary>
    public CustomerId CustomerId { get; private set; }

    /// <summary>Human-friendly order identifier.</summary>
    public string OrderNumber { get; private set; }

    // ===== Status Flags (State Machine) =====

    /// <summary>Order has been created and placed.</summary>
    public bool IsPlaced { get; private set; }

    /// <summary>Customer has confirmed the order.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>Payment has been received.</summary>
    public bool IsPaid { get; private set; }

    /// <summary>Order has been shipped.</summary>
    public bool IsShipped { get; private set; }

    /// <summary>Order has been cancelled.</summary>
    public bool IsCancelled { get; private set; }

    // ===== Content =====

    /// <summary>Line items in this order.</summary>
    public List<LineItem> LineItems { get; private set; }

    // ===== Audit Trail =====

    /// <summary>When the order was placed.</summary>
    public DateTime PlacedAt { get; private set; }

    /// <summary>When the order was confirmed (null if not confirmed).</summary>
    public DateTime? ConfirmedAt { get; private set; }

    /// <summary>When payment was received (null if not paid).</summary>
    public DateTime? PaidAt { get; private set; }

    /// <summary>When the order was shipped (null if not shipped).</summary>
    public DateTime? ShippedAt { get; private set; }

    // ===== Shipping Information =====

    /// <summary>Tracking number for shipment.</summary>
    public string? TrackingNumber { get; private set; }

    // ===== Derived Properties =====

    /// <summary>Total cost of order (sum of line items).</summary>
    public decimal Total => CalculateTotal();

    /// <summary>Number of line items.</summary>
    public int LineItemCount => LineItems?.Count ?? 0;

    /// <summary>Get the order status as a string.</summary>
    public string Status => GetStatus();

    /// <summary>Is the order still open for modification?</summary>
    public bool IsOpen => IsPlaced && !IsConfirmed && !IsCancelled;

    /// <summary>Is the order in a terminal state (can't be modified)?</summary>
    public bool IsTerminal => IsCancelled || (IsShipped);

    // ===== Static Initialization =====

    public static OrderState Initial => default;

    // ===== Event Application Methods =====

    /// <summary>
    /// Apply OrderPlacedEvent: Initialize order with line items.
    /// </summary>
    internal OrderState Apply(OrderPlacedEvent e)
    {
        // Create new state with applied changes
        var newState = this with
        {
            OrderNumber = e.OrderNumber,
            CustomerId = e.CustomerId,
            IsPlaced = true,
            PlacedAt = DateTime.UtcNow,
            LineItems = new List<LineItem>(e.LineItems)
        };

        // Validate invariants
        ThrowIfInvalid(newState);

        return newState;
    }

    /// <summary>
    /// Apply OrderConfirmedEvent: Mark order as confirmed.
    /// </summary>
    internal OrderState Apply(OrderConfirmedEvent _)
    {
        var newState = this with
        {
            IsConfirmed = true,
            ConfirmedAt = DateTime.UtcNow
        };

        ThrowIfInvalid(newState);
        return newState;
    }

    /// <summary>
    /// Apply PaymentProcessedEvent: Mark order as paid.
    /// </summary>
    internal OrderState Apply(PaymentProcessedEvent e)
    {
        var newState = this with
        {
            IsPaid = true,
            PaidAt = DateTime.UtcNow
        };

        ThrowIfInvalid(newState);
        return newState;
    }

    /// <summary>
    /// Apply OrderShippedEvent: Mark order as shipped with tracking.
    /// </summary>
    internal OrderState Apply(OrderShippedEvent e)
    {
        var newState = this with
        {
            IsShipped = true,
            ShippedAt = DateTime.UtcNow,
            TrackingNumber = e.TrackingNumber
        };

        ThrowIfInvalid(newState);
        return newState;
    }

    /// <summary>
    /// Apply OrderCancelledEvent: Mark order as cancelled.
    /// </summary>
    internal OrderState Apply(OrderCancelledEvent _)
    {
        var newState = this with { IsCancelled = true };

        ThrowIfInvalid(newState);
        return newState;
    }

    // ===== Helper Methods =====

    /// <summary>Calculate total from line items.</summary>
    private decimal CalculateTotal()
    {
        if (LineItems == null || LineItems.Count == 0)
            return 0;

        return LineItems.Sum(li => li.Total);
    }

    /// <summary>Get human-readable order status.</summary>
    private string GetStatus()
    {
        if (IsCancelled) return "Cancelled";
        if (IsShipped) return "Shipped";
        if (IsPaid) return "Paid";
        if (IsConfirmed) return "Confirmed";
        if (IsPlaced) return "Placed";
        return "New";
    }

    /// <summary>
    /// Validate state invariants.
    /// These are business rules that should always be true.
    /// Called after each state transition.
    /// </summary>
    private static void ThrowIfInvalid(OrderState state)
    {
        // Invariant 1: If confirmed, must be placed
        if (state.IsConfirmed && !state.IsPlaced)
            throw new InvalidOperationException("Cannot be confirmed without being placed");

        // Invariant 2: If paid, must be confirmed
        if (state.IsPaid && !state.IsConfirmed)
            throw new InvalidOperationException("Cannot be paid without being confirmed");

        // Invariant 3: If shipped, must be paid
        if (state.IsShipped && !state.IsPaid)
            throw new InvalidOperationException("Cannot be shipped without being paid");

        // Invariant 4: Can't be both cancelled and anything else
        if (state.IsCancelled && (state.IsConfirmed || state.IsPaid || state.IsShipped))
            throw new InvalidOperationException("Cancelled order cannot have other states");

        // Invariant 5: Must have at least one line item if placed
        if (state.IsPlaced && (state.LineItems == null || state.LineItems.Count == 0))
            throw new InvalidOperationException("Placed order must have at least one line item");

        // Invariant 6: Total must be positive if placed
        if (state.IsPlaced && state.Total <= 0)
            throw new InvalidOperationException("Order total must be positive");

        // Invariant 7: Timestamps must be in order
        if (state.PlacedAt > DateTime.MinValue)
        {
            if (state.ConfirmedAt.HasValue && state.ConfirmedAt < state.PlacedAt)
                throw new InvalidOperationException("ConfirmedAt cannot be before PlacedAt");

            if (state.PaidAt.HasValue && state.PaidAt < state.PlacedAt)
                throw new InvalidOperationException("PaidAt cannot be before PlacedAt");

            if (state.ShippedAt.HasValue && state.ShippedAt < state.PlacedAt)
                throw new InvalidOperationException("ShippedAt cannot be before PlacedAt");
        }
    }

    /// <summary>
    /// Get a summary of the order state for logging/display.
    /// </summary>
    public override string ToString() =>
        $"Order {OrderNumber}: {Status}, ${Total:F2}, {LineItemCount} items, customer {CustomerId.Value}";

    /// <summary>
    /// Get detailed information about the order.
    /// </summary>
    public string GetDetailedSummary()
    {
        var details = new System.Text.StringBuilder();

        details.AppendLine($"Order: {OrderNumber}");
        details.AppendLine($"Customer: {CustomerId.Value}");
        details.AppendLine($"Status: {Status}");
        details.AppendLine($"Total: ${Total:F2}");

        if (LineItems != null && LineItems.Count > 0)
        {
            details.AppendLine("Line Items:");
            foreach (var item in LineItems)
            {
                details.AppendLine($"  - Product {item.ProductId.Value}: {item.Quantity} x ${item.UnitPrice:F2} = ${item.Total:F2}");
            }
        }

        details.AppendLine($"Placed: {PlacedAt:yyyy-MM-dd HH:mm:ss}");
        if (ConfirmedAt.HasValue)
            details.AppendLine($"Confirmed: {ConfirmedAt:yyyy-MM-dd HH:mm:ss}");
        if (PaidAt.HasValue)
            details.AppendLine($"Paid: {PaidAt:yyyy-MM-dd HH:mm:ss}");
        if (ShippedAt.HasValue)
            details.AppendLine($"Shipped: {ShippedAt:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrEmpty(TrackingNumber))
            details.AppendLine($"Tracking: {TrackingNumber}");

        return details.ToString();
    }
}
