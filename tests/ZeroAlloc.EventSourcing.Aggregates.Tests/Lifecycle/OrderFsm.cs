#pragma warning disable ZSM0003 // Place/Ship triggers each appear in only one transition by design — the lifecycle is linear (Draft → Placed → Shipped) and these single-edge triggers are intentional

using ZeroAlloc.StateMachine;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests.Lifecycle;

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
