using System;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.StateMachine;

namespace ZeroAlloc.EventSourcing.AotSmoke;

#pragma warning disable MA0048 // co-located domain types for a compact sample

public readonly record struct OrderId(Guid Value);

public enum OrderStatus { Draft, Placed, Shipped, Cancelled }
public enum OrderTrigger { Place, Ship, Cancel }

public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderShippedEvent(string TrackingNumber);
public record OrderCancelledEvent(string Reason);

#pragma warning disable ZSM0003 // Place/Ship triggers each appear in only one transition by design — the lifecycle is linear (Draft → Placed → Shipped) and these single-edge triggers are intentional
[StateMachine(InitialState = nameof(OrderStatus.Draft))]
[Transition<OrderStatus, OrderTrigger>(From = OrderStatus.Draft,  On = OrderTrigger.Place,  To = OrderStatus.Placed)]
[Transition<OrderStatus, OrderTrigger>(From = OrderStatus.Placed, On = OrderTrigger.Ship,   To = OrderStatus.Shipped)]
[Transition<OrderStatus, OrderTrigger>(From = OrderStatus.Draft,  On = OrderTrigger.Cancel, To = OrderStatus.Cancelled)]
[Transition<OrderStatus, OrderTrigger>(From = OrderStatus.Placed, On = OrderTrigger.Cancel, To = OrderStatus.Cancelled)]
[Terminal<OrderStatus>(State = OrderStatus.Shipped)]
[Terminal<OrderStatus>(State = OrderStatus.Cancelled)]
public sealed partial class OrderFsm
{
    public OrderFsm(OrderStatus current) => _state = current;
}
#pragma warning restore ZSM0003

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public OrderStatus Status { get; private set; }
    public bool IsPlaced => Status == OrderStatus.Placed || Status == OrderStatus.Shipped;
    public bool IsShipped => Status == OrderStatus.Shipped;
    public bool IsCancelled => Status == OrderStatus.Cancelled;
    public decimal Total { get; private set; }

    internal OrderState Apply(OrderPlacedEvent e)    => this with { Status = OrderStatus.Placed,    Total = e.Total };
    internal OrderState Apply(OrderShippedEvent _)   => this with { Status = OrderStatus.Shipped };
    internal OrderState Apply(OrderCancelledEvent _) => this with { Status = OrderStatus.Cancelled };
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total)
    {
        var fsm = new OrderFsm(State.Status);
        if (!fsm.TryFire(OrderTrigger.Place))
            throw new InvalidOperationException($"Cannot place order in status {State.Status}.");
        Raise(new OrderPlacedEvent(orderId, total));
    }

    public void Ship(string tracking)
    {
        var fsm = new OrderFsm(State.Status);
        if (!fsm.TryFire(OrderTrigger.Ship))
            throw new InvalidOperationException($"Cannot ship order in status {State.Status}.");
        Raise(new OrderShippedEvent(tracking));
    }

    public void Cancel(string reason)
    {
        var fsm = new OrderFsm(State.Status);
        if (!fsm.TryFire(OrderTrigger.Cancel))
            throw new InvalidOperationException($"Cannot cancel order in status {State.Status}.");
        Raise(new OrderCancelledEvent(reason));
    }

    public void SetId(OrderId id) => Id = id;

    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent p => state.Apply(p),
        OrderShippedEvent s => state.Apply(s),
        OrderCancelledEvent c => state.Apply(c),
        _ => state,
    };
}

#pragma warning restore MA0048
