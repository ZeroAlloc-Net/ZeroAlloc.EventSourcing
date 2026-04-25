using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests.Lifecycle;

public readonly record struct OrderId(Guid Value);

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    // FSM-validated command methods are added test-by-test in Tasks 7, 9, 11.
    public void SetId(OrderId id) => Id = id;

    public void PlaceOrder(decimal total)
    {
        var fsm = new OrderFsm(State.Status);
        if (!fsm.TryFire(OrderTrigger.Place))
            throw new InvalidOperationException(
                $"Cannot place order in status {State.Status}.");
        Raise(new OrderPlaced(total));
    }

    public void Ship(string trackingNumber)
    {
        var fsm = new OrderFsm(State.Status);
        if (!fsm.TryFire(OrderTrigger.Ship))
            throw new InvalidOperationException(
                $"Cannot ship order in status {State.Status}.");
        Raise(new OrderShipped(trackingNumber));
    }

    public void Cancel(string reason)
    {
        var fsm = new OrderFsm(State.Status);
        if (!fsm.TryFire(OrderTrigger.Cancel))
            throw new InvalidOperationException(
                $"Cannot cancel order in status {State.Status}.");
        Raise(new OrderCancelled(reason));
    }
}
