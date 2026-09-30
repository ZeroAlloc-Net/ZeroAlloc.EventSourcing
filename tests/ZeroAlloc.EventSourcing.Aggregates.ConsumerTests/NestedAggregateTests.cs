using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

// Two aggregates with the same name, nested in static classes per bounded context, as
// docs/usage-guides/building-aggregates.md shows. The generator emits ApplyEvent into each nested
// Order and one registry per aggregate: Retail_OrderEventTypeRegistry and
// Wholesale_OrderEventTypeRegistry. Issue #406.
public static partial class Retail
{
    public sealed partial class Order : Aggregate<OrderId, OrderState>
    {
        public void Place(string customer) => Raise(new OrderPlaced(customer));
    }
}

public static partial class Wholesale
{
    public sealed partial class Order : Aggregate<OrderId, OrderState>
    {
        public void Ship(string tracking) => Raise(new OrderShipped(tracking));
    }
}

public sealed class NestedAggregateTests
{
    [Fact]
    public async Task NestedAggregates_RoundTripThroughTheirGeneratedRegistries()
    {
        var id = new OrderId(Guid.NewGuid());
        var store = new EventStore(
            new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new Retail_OrderEventTypeRegistry());
        var repository = new AggregateRepository<Retail.Order, OrderId>(
            store, static () => new Retail.Order(), static i => new StreamId($"retail-{i.Value:N}"));

        var order = new Retail.Order();
        order.Place("alice");
        (await repository.SaveAsync(order, id)).IsSuccess.Should().BeTrue();

        var loaded = await repository.LoadAsync(id);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.State.IsPlaced.Should().BeTrue();

        var wholesale = new Wholesale.Order();
        wholesale.Ship("track-1");
        wholesale.State.IsShipped.Should().BeTrue();
        new Wholesale_OrderEventTypeRegistry().TryGetType(nameof(OrderShipped), out var type).Should().BeTrue();
        type.Should().Be<OrderShipped>();
    }
}
