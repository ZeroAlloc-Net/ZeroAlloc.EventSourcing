using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

// An aggregate split over two partial declarations that both repeat the base list. Before the fix
// for issue 400 the generators emitted twice under one hint name, Roslyn dropped their whole output
// and every aggregate in this project failed to compile with CS0534.
public sealed partial class ShipmentAggregate : Aggregate<ShipmentId, ShipmentState>
{
    public void Dispatch(string carrier) => Raise(new ShipmentDispatched(carrier));
}

public sealed partial class ShipmentAggregate : Aggregate<ShipmentId, ShipmentState>
{
    public void Deliver() => Raise(new ShipmentDelivered());
}

public readonly record struct ShipmentId(Guid Value);
public record ShipmentDispatched(string Carrier);
public record ShipmentDelivered();

public struct ShipmentState : IAggregateState<ShipmentState>
{
    public static ShipmentState Initial => default;
    public string? Carrier { get; private set; }
    public bool IsDelivered { get; private set; }

    internal ShipmentState Apply(ShipmentDispatched e) => this with { Carrier = e.Carrier };
    internal ShipmentState Apply(ShipmentDelivered _) => this with { IsDelivered = true };
}

public class SplitAggregateTests
{
    [Fact]
    public void GeneratedApplyEvent_RoutesEventsRaisedFromEitherDeclaration()
    {
        var shipment = new ShipmentAggregate();
        shipment.Dispatch("DHL");
        shipment.Deliver();

        shipment.State.Carrier.Should().Be("DHL");
        shipment.State.IsDelivered.Should().BeTrue();
    }

    [Fact]
    public void GeneratedRegistry_CoversEveryEventOfTheSplitAggregate()
    {
        var registry = new ShipmentAggregateEventTypeRegistry();

        registry.TryGetType("ShipmentDispatched", out var dispatched).Should().BeTrue();
        dispatched.Should().Be(typeof(ShipmentDispatched));
        registry.TryGetType("ShipmentDelivered", out var delivered).Should().BeTrue();
        delivered.Should().Be(typeof(ShipmentDelivered));
    }
}
