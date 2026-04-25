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
}
