using System.Linq;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.EventSourcing.Generators.Tests;

/// <summary>
/// A nested aggregate or projection is generated into the real type, through partial declarations
/// of its containing types. A nested type whose containing type is not partial gets ZAES005, a
/// generic one gets ZAES006, and neither is generated. Every case checks that an unrelated
/// top-level aggregate still generates. Regression tests for issue 406.
/// </summary>
public class NestedAndGenericTypeTests
{
    // Shared events, state and an unrelated top-level aggregate, Cart.
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

    private const string OrderBody = """
        {
            public void Place(string c) => Raise(new OrderPlaced(c));
            public void Ship() => Raise(new OrderShipped());
        }
        """;

    private static readonly string[] CartSources = ["Shop.Cart.ApplyEvent.g.cs", "Shop.CartEventTypeRegistry.g.cs"];

    [Fact]
    public void NestedAggregatesWithTheSameName_AreGeneratedIntoTheRealTypes()
    {
        var nested = $$"""
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public static partial class Retail
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState>
                {{OrderBody}}
            }
            public static partial class Wholesale
            {
                internal sealed partial class Order : Aggregate<OrderId, OrderState>
                {{OrderBody}}
            }
            """;
        var usage = """
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            internal static class Usage
            {
                public static bool Run()
                {
                    var retail = new Retail.Order();
                    retail.Place("a");
                    var wholesale = new Wholesale.Order();
                    wholesale.Ship();
                    IEventTypeRegistry r1 = new Retail_OrderEventTypeRegistry();
                    IEventTypeRegistry r2 = new Wholesale_OrderEventTypeRegistry();
                    return retail.State.IsPlaced && wholesale.State.IsShipped
                        && r1.TryGetType("OrderPlaced", out _) && r2.TryGetType("OrderShipped", out _);
                }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested, usage);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(
            [
                "Shop.Retail.Order.ApplyEvent.g.cs",
                "Shop.Retail.OrderEventTypeRegistry.g.cs",
                "Shop.Wholesale.Order.ApplyEvent.g.cs",
                "Shop.Wholesale.OrderEventTypeRegistry.g.cs",
                .. CartSources,
            ]);
    }

    [Fact]
    public void DoublyNestedAggregate_ReopensEveryContainingTypeWithItsKindAndAccessibility()
    {
        var nested = $$"""
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            internal readonly partial struct Sales
            {
                public partial record Orders
                {
                    private sealed partial class Order : Aggregate<OrderId, OrderState>
                    {{OrderBody}}

                    public static bool Run()
                    {
                        var order = new Order();
                        order.Place("a");
                        return order.State.IsPlaced;
                    }
                }
            }
            """;
        var usage = """
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            internal static class Usage
            {
                public static bool Run()
                {
                    IEventTypeRegistry registry = new Sales_Orders_OrderEventTypeRegistry();
                    return Sales.Orders.Run() && registry.TryGetType("OrderPlaced", out _);
                }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested, usage);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(
            [
                "Shop.Sales.Orders.Order.ApplyEvent.g.cs",
                "Shop.Sales.Orders.OrderEventTypeRegistry.g.cs",
                .. CartSources,
            ]);

        run.Source("Shop.Sales.Orders.Order.ApplyEvent.g.cs").ReplaceLineEndings().Should().Be("""
            // <auto-generated/>
            #nullable enable

            namespace Shop;

            internal partial struct Sales
            {
                public partial record Orders
                {
                    private partial class Order
                    {
                        protected override Shop.OrderState ApplyEvent(Shop.OrderState state, object @event)
                            => @event switch
                            {
                                Shop.OrderPlaced __e => state.Apply(__e),
                                Shop.OrderShipped __e => state.Apply(__e),
                                _ => state
                            };
                    }
                }
            }

            """.ReplaceLineEndings());
        run.Source("Shop.Sales.Orders.OrderEventTypeRegistry.g.cs").Should().Contain(
            "for <see cref=\"Sales.Orders.Order\"/>.</summary>");
    }

    [Fact]
    public void AggregateNestedInAGenericType_IsGeneratedIntoTheRealType()
    {
        var nested = $$"""
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public partial class Module<TTenant, @class> where TTenant : class
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState>
                {{OrderBody}}
            }
            """;
        var usage = """
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            internal static class Usage
            {
                public static bool Run()
                {
                    var order = new Module<string, int>.Order();
                    order.Place("a");
                    IEventTypeRegistry registry = new Module2_OrderEventTypeRegistry();
                    return order.State.IsPlaced && registry.TryGetType("OrderPlaced", out _);
                }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested, usage);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(
            [
                "Shop.Module`2.Order.ApplyEvent.g.cs",
                "Shop.Module`2.OrderEventTypeRegistry.g.cs",
                .. CartSources,
            ]);
        run.Source("Shop.Module`2.Order.ApplyEvent.g.cs").Should().Contain("public partial class Module<TTenant, @class>");
    }

    [Fact]
    public void AggregatesNestedInGenericTypesOfTheSameNameAndDifferentArity_GetDistinctRegistries()
    {
        var nested = $$"""
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public partial class Module<T>
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState>
                {{OrderBody}}
            }
            public partial class Module<T, U>
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState>
                {{OrderBody}}
            }
            """;
        var usage = """
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            internal static class Usage
            {
                public static bool Run()
                {
                    IEventTypeRegistry one = new Module1_OrderEventTypeRegistry();
                    IEventTypeRegistry two = new Module2_OrderEventTypeRegistry();
                    return one.TryGetType("OrderPlaced", out _) && two.TryGetType("OrderShipped", out _);
                }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested, usage);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(
            [
                "Shop.Module`1.Order.ApplyEvent.g.cs",
                "Shop.Module`1.OrderEventTypeRegistry.g.cs",
                "Shop.Module`2.Order.ApplyEvent.g.cs",
                "Shop.Module`2.OrderEventTypeRegistry.g.cs",
                .. CartSources,
            ]);
    }

    [Fact]
    public void AggregateNestedInAGenericTypeWithEventsOfItsTypeParameter_ReportsZAES006()
    {
        // The registry sits at namespace level, where it cannot name an event type that uses a type
        // parameter of the containing type.
        var nested = """
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public partial class Module<T>
            {
                public record Placed(T Value);
                public struct State : IAggregateState<State>
                {
                    public static State Initial => default;
                    internal State Apply(Placed e) => this;
                }
                public sealed partial class Order : Aggregate<OrderId, State> { }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested);

        run.Exceptions.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("ZAES006");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("Shop.Module<T>.Order");
        run.HintNames.Should().BeEquivalentTo(CartSources);
    }

    [Fact]
    public void AggregateInANonPartialContainingType_ReportsZAES005AndGeneratesNothingForIt()
    {
        // Outer is not partial; Middle is. The warning names the outermost non-partial type.
        var nested = $$"""
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public static class Outer
            {
                public static partial class Middle
                {
                    public sealed partial class Order : Aggregate<OrderId, OrderState>
                    {{OrderBody}}
                }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested);

        run.Exceptions.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("ZAES005");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Be(
            "Aggregate or projection 'Shop.Outer.Middle.Order' is nested in 'Shop.Outer', which is not partial, "
            + "so no code is generated for it. Declare every containing type partial.");
        run.LocatedText(diagnostic).Should().Be("Order");
        run.HintNames.Should().BeEquivalentTo(CartSources);
        // Without the generated ApplyEvent the aggregate stays abstract; nothing else fails.
        run.Errors.Select(e => e.Id).Should().BeEquivalentTo(["CS0534"]);
    }

    [Fact]
    public void GenericAggregate_ReportsZAES006AndGeneratesNothingForIt()
    {
        var generic = $$"""
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public sealed partial class Order<TTag> : Aggregate<OrderId, OrderState>
            {{OrderBody}}
            """;

        var run = GeneratorHarness.Run(Domain, generic);

        run.Exceptions.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("ZAES006");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Be(
            "Aggregate or projection 'Shop.Order<TTag>' is generic, or handles events whose types use a type "
            + "parameter, so no code is generated for it. Write its dispatch method by hand.");
        run.LocatedText(diagnostic).Should().Be("Order");
        run.HintNames.Should().BeEquivalentTo(CartSources);
    }

    [Fact]
    public void GenericAggregateWithAHandWrittenApplyEvent_ReportsNothing()
    {
        var generic = """
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            public sealed partial class Order<TTag> : Aggregate<OrderId, OrderState>
            {
                protected override OrderState ApplyEvent(OrderState state, object @event) => state;
            }
            """;

        var run = GeneratorHarness.Run(Domain, generic);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(CartSources);
    }

    private const string ProjectionModel = """
        namespace Shop;
        public record OrderView(int Count);
        public record ItemAdded();
        """;

    private static string ProjectionBody(string view) => $$"""
        {
            private {{view}} Apply({{view}} current, ItemAdded e) => current with { Count = current.Count + 1 };
            protected override {{view}} Apply({{view}} current, ZeroAlloc.EventSourcing.EventEnvelope @event)
                => ApplyTyped(current, @event.Event);
        }
        """;

    [Fact]
    public void NestedProjections_AreGeneratedIntoTheRealTypes()
    {
        var nested = $$"""
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            public partial class Reporting
            {
                internal partial struct Views
                {
                    public sealed partial class Summary : Projection<OrderView>
                    {{ProjectionBody("OrderView")}}
                }
            }
            public static partial class Other
            {
                public sealed partial class Summary : Projection<OrderView>
                {{ProjectionBody("OrderView")}}
            }
            public partial class Generic<T>
            {
                public sealed partial class Summary : Projection<OrderView>
                {{ProjectionBody("OrderView")}}
            }
            """;

        var run = GeneratorHarness.Run(Domain, ProjectionModel, nested);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(
            [
                "Shop.Reporting.Views.Summary.ApplyTyped.g.cs",
                "Shop.Other.Summary.ApplyTyped.g.cs",
                "Shop.Generic`1.Summary.ApplyTyped.g.cs",
                .. CartSources,
            ]);
    }

    [Fact]
    public void ProjectionInANonPartialContainingType_ReportsZAES005()
    {
        var nested = $$"""
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            public class Reporting
            {
                public sealed partial class Summary : Projection<OrderView>
                {{ProjectionBody("OrderView")}}
            }
            """;

        var run = GeneratorHarness.Run(Domain, ProjectionModel, nested);

        run.Exceptions.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("ZAES005");
        diagnostic.GetMessage().Should().Contain("'Shop.Reporting.Summary' is nested in 'Shop.Reporting'");
        run.HintNames.Should().BeEquivalentTo(CartSources);
    }

    [Fact]
    public void GenericProjection_ReportsZAES006AndGeneratesNothingForIt()
    {
        var generic = $$"""
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            public sealed partial class Summary<T> : Projection<OrderView>
            {{ProjectionBody("OrderView")}}
            """;

        var run = GeneratorHarness.Run(Domain, ProjectionModel, generic);

        run.Exceptions.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("ZAES006");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("'Shop.Summary<T>' is generic");
        run.HintNames.Should().BeEquivalentTo(CartSources);
    }
}
