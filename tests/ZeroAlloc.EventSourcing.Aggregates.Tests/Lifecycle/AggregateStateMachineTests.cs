using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests.Lifecycle;

public class AggregateStateMachineTests
{
    [Fact]
    public void Place_FromDraft_Succeeds()
    {
        var order = new Order();

        order.PlaceOrder(total: 100m);

        order.State.Status.Should().Be(OrderStatus.Placed);
        order.State.Total.Should().Be(100m);
        order.Version.Value.Should().Be(1);
    }

    [Fact]
    public void Place_FromPlaced_Throws()
    {
        var order = new Order();
        order.PlaceOrder(total: 100m);

        var act = () => order.PlaceOrder(total: 50m);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Placed*");
        order.State.Total.Should().Be(100m); // unchanged
    }
}
