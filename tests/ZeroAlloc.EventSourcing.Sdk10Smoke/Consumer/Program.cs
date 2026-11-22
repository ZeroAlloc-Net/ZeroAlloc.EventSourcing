using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.Sdk10Smoke;

// OrderEventTypeRegistry exists only if the compiler loaded the generator.
var registry = new OrderEventTypeRegistry();
if (!registry.TryGetType(nameof(OrderPlacedEvent), out var type) || type != typeof(OrderPlacedEvent))
{
    Console.Error.WriteLine("FAIL: the generated registry does not know OrderPlacedEvent");
    return 1;
}

Console.WriteLine("SDK 10 consumer: generator loaded, event type registry emitted.");
return 0;

namespace ZeroAlloc.EventSourcing.Sdk10Smoke
{
    public readonly record struct OrderId(Guid Value);

    public sealed record OrderPlacedEvent(string OrderId, decimal Total);

    public partial struct OrderState : IAggregateState<OrderState>
    {
        public static OrderState Initial => default;

        public decimal Total { get; private set; }

        internal OrderState Apply(OrderPlacedEvent e) => this with { Total = e.Total };
    }

    public sealed partial class Order : Aggregate<OrderId, OrderState>
    {
        public void Place(string orderId, decimal total) => Raise(new OrderPlacedEvent(orderId, total));
    }
}
