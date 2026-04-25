using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests.Lifecycle;

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default; // Status defaults to OrderStatus.Draft (enum value 0)

    public OrderStatus Status { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    public string? CancelReason { get; private set; }

    internal OrderState Apply(OrderPlaced e)    => this with { Status = OrderStatus.Placed,    Total = e.Total };
    internal OrderState Apply(OrderShipped e)   => this with { Status = OrderStatus.Shipped,   TrackingNumber = e.TrackingNumber };
    internal OrderState Apply(OrderCancelled e) => this with { Status = OrderStatus.Cancelled, CancelReason = e.Reason };
}
