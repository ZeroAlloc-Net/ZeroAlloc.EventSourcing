using System.Linq;
using AwesomeAssertions;

namespace ZeroAlloc.EventSourcing.Generators.Tests;

/// <summary>
/// An aggregate or projection split over several partial declarations must produce exactly one
/// generated source per generator, whichever declarations repeat the base list, and one bad type
/// must never stop generation for the others. Regression tests for issue 400.
/// </summary>
public class PartialDeclarationTests
{
    private const string Domain = """
        using ZeroAlloc.EventSourcing.Aggregates;
        namespace Shop;

        public readonly record struct OrderId(System.Guid Value);
        public record OrderPlaced(string Customer);
        public record OrderShipped();
        public struct OrderState : IAggregateState<OrderState>
        {
            public static OrderState Initial => default;
            public bool IsPlaced { get; private set; }
            public bool IsShipped { get; private set; }
            internal OrderState Apply(OrderPlaced e) => this with { IsPlaced = true };
            internal OrderState Apply(OrderShipped e) => this with { IsShipped = true };
        }

        // An unrelated aggregate in the same project: it must keep its generated code.
        public readonly record struct CartId(System.Guid Value);
        public record CartOpened();
        public struct CartState : IAggregateState<CartState>
        {
            public static CartState Initial => default;
            public bool IsOpen { get; private set; }
            internal CartState Apply(CartOpened e) => this with { IsOpen = true };
        }
        public sealed partial class Cart : Aggregate<CartId, CartState>
        {
            public void Open() => Raise(new CartOpened());
        }
        """;

    private const string WithBase = "public sealed partial class Order : Aggregate<OrderId, OrderState>";
    private const string WithoutBase = "public sealed partial class Order";

    private static string OrderPart(string header, string member) => $$"""
        using ZeroAlloc.EventSourcing.Aggregates;
        namespace Shop;
        {{header}}
        {
            {{member}}
        }
        """;

    [Theory]
    [InlineData(WithBase, WithBase)]
    [InlineData(WithBase, WithoutBase)]
    [InlineData(WithoutBase, WithBase)]
    public void SplitAggregate_GeneratesOnceAndKeepsOtherAggregates(string firstHeader, string secondHeader)
    {
        var run = GeneratorHarness.Run(
            Domain,
            OrderPart(firstHeader, "public void Place(string c) => Raise(new OrderPlaced(c));"),
            OrderPart(secondHeader, "public void Ship() => Raise(new OrderShipped());"));

        run.Exceptions.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(
            "Shop.Order.ApplyEvent.g.cs",
            "Shop.OrderEventTypeRegistry.g.cs",
            "Shop.Cart.ApplyEvent.g.cs",
            "Shop.CartEventTypeRegistry.g.cs");
    }

    [Fact]
    public void AggregateSplitOverThreeDeclarations_GeneratesOnce()
    {
        var run = GeneratorHarness.Run(
            Domain,
            OrderPart(WithoutBase, "public void Place(string c) => Raise(new OrderPlaced(c));"),
            OrderPart(WithBase, "public void Ship() => Raise(new OrderShipped());"),
            OrderPart(WithBase, "public bool Shipped => State.IsShipped;"));

        run.Exceptions.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Count(h => h.StartsWith("Shop.Order", System.StringComparison.Ordinal)).Should().Be(2);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SplitProjection_GeneratesOnce(bool firstRepeatsBase, bool secondRepeatsBase)
    {
        const string header = "public sealed partial class OrderSummary";
        const string baseList = " : Projection<OrderView>";
        var model = """
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            public record OrderView(int Count);
            public record ItemAdded();
            public record ItemRemoved();
            """;
        var first = $$"""
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            {{header}}{{(firstRepeatsBase ? baseList : "")}}
            {
                private OrderView Apply(OrderView current, ItemAdded e) => current with { Count = current.Count + 1 };
                protected override OrderView Apply(OrderView current, EventEnvelope @event) => ApplyTyped(current, @event.Event);
            }
            """;
        var second = $$"""
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            {{header}}{{(secondRepeatsBase ? baseList : "")}}
            {
                private OrderView Apply(OrderView current, ItemRemoved e) => current with { Count = current.Count - 1 };
            }
            """;

        var run = GeneratorHarness.Run(model, first, second);

        run.Exceptions.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo("Shop.OrderSummary.ApplyTyped.g.cs");
    }

    [Fact]
    public void AggregatesWithTheSameNameNestedInDifferentTypes_DoNotCollide()
    {
        // Two aggregates share a short name and a namespace. Their hint names must still differ,
        // otherwise AddSource throws and every aggregate in the project loses its generated code.
        var nested = """
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public static partial class Retail
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState> { }
            }
            public static partial class Wholesale
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState> { }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.HintNames.Should().Contain(["Shop.Cart.ApplyEvent.g.cs", "Shop.CartEventTypeRegistry.g.cs"]);
        run.HintNames.Should().OnlyHaveUniqueItems();
    }
}
