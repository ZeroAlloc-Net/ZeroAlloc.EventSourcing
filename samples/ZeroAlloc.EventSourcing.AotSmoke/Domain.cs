using System;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.AotSmoke;

#pragma warning disable MA0048 // co-located domain types for a compact sample

public readonly record struct OrderId(Guid Value);

public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderShippedEvent(string TrackingNumber);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public bool IsPlaced { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }

    internal OrderState Apply(OrderPlacedEvent e) => this with { IsPlaced = true, Total = e.Total };
    internal OrderState Apply(OrderShippedEvent _) => this with { IsShipped = true };
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total) => Raise(new OrderPlacedEvent(orderId, total));
    public void Ship(string tracking) => Raise(new OrderShippedEvent(tracking));
    public void SetId(OrderId id) => Id = id;

    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent p => state.Apply(p),
        OrderShippedEvent s => state.Apply(s),
        _ => state,
    };
}

#pragma warning restore MA0048
