using System.Linq;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.EventSourcing.Generators.Tests;

/// <summary>
/// A generated file cannot extend a file-local type, so a file-local aggregate or projection, or one
/// nested in a file-local type, gets the error ZAES007 and no generated code. Every case checks that
/// an unrelated aggregate still generates. Regression tests for issue 417.
/// </summary>
public class FileLocalTypeTests
{
    // Shared events and state, and an unrelated aggregate, Cart, that must keep its generated code.
    private const string Domain = """
        using ZeroAlloc.EventSourcing.Aggregates;
        namespace Shop;

        public readonly record struct OrderId(System.Guid Value);
        public record OrderPlaced(string Customer);
        public struct OrderState : IAggregateState<OrderState>
        {
            public static OrderState Initial => default;
            public bool IsPlaced { get; private set; }
            internal OrderState Apply(OrderPlaced e) => this with { IsPlaced = true };
        }

        public readonly record struct CartId(System.Guid Value);
        public record CartOpened();
        public struct CartState : IAggregateState<CartState>
        {
            public static CartState Initial => default;
            internal CartState Apply(CartOpened e) => this;
        }
        public sealed partial class Cart : Aggregate<CartId, CartState>
        {
            public void Open() => Raise(new CartOpened());
        }
        """;

    private static readonly string[] CartSources = ["Shop.Cart.ApplyEvent.g.cs", "Shop.CartEventTypeRegistry.g.cs"];

    private static Diagnostic SingleZaes007(GeneratorRun run)
    {
        run.Exceptions.Should().BeEmpty();
        var diagnostic = run.GeneratorDiagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("ZAES007");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        run.LocatedText(diagnostic).Should().Be("Order");
        run.HintNames.Should().BeEquivalentTo(CartSources);
        return diagnostic;
    }

    [Fact]
    public void FileLocalAggregate_ReportsZAES007AndGeneratesNothingForIt()
    {
        var fileLocal = """
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            file sealed partial class Order : Aggregate<OrderId, OrderState>
            {
                public void Place(string c) => Raise(new OrderPlaced(c));
            }
            """;

        var run = GeneratorHarness.Run(Domain, fileLocal);

        SingleZaes007(run).GetMessage().Should().Be(
            "Aggregate or projection 'Shop.Order' is file-local or nested in a file-local type, so no code is "
            + "generated for it. A generated file cannot extend a file-local type; remove the file modifier, "
            + "or declare the type without partial and write its dispatch by hand.");
        // Nothing is generated into a stray class: the only other error is the missing ApplyEvent.
        run.Errors.Where(e => e.Id != "ZAES007").Select(e => e.Id).Should().BeEquivalentTo(["CS0534"]);
    }

    [Fact]
    public void AggregateNestedInAFileLocalType_ReportsZAES007()
    {
        var nested = """
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            file static partial class Retail
            {
                public sealed partial class Order : Aggregate<OrderId, OrderState> { }
            }
            """;

        var run = GeneratorHarness.Run(Domain, nested);

        SingleZaes007(run).GetMessage().Should().Contain("'Shop.Retail.Order' is file-local or nested in a file-local type");
    }

    [Fact]
    public void FileLocalProjection_ReportsZAES007AndGeneratesNothingForIt()
    {
        var projection = """
            using ZeroAlloc.EventSourcing;
            namespace Shop;
            public record OrderView(int Count);
            file sealed partial class Order : Projection<OrderView>
            {
                private OrderView Apply(OrderView current, OrderPlaced e) => current with { Count = current.Count + 1 };
                protected override OrderView Apply(OrderView current, EventEnvelope @event) => current;
            }
            """;

        var run = GeneratorHarness.Run(Domain, projection);

        SingleZaes007(run);
        run.Errors.Where(e => e.Id != "ZAES007").Should().BeEmpty();
    }

    [Fact]
    public void FileLocalTypeWithoutPartial_ReportsNothing()
    {
        // The documented way out: a type that is not partial is not looked at.
        var handWritten = """
            using ZeroAlloc.EventSourcing.Aggregates;
            namespace Shop;
            file sealed class Order : Aggregate<OrderId, OrderState>
            {
                protected override OrderState ApplyEvent(OrderState state, object @event) => state;
            }
            """;

        var run = GeneratorHarness.Run(Domain, handWritten);

        run.Exceptions.Should().BeEmpty();
        run.GeneratorDiagnostics.Should().BeEmpty();
        run.Errors.Should().BeEmpty();
        run.HintNames.Should().BeEquivalentTo(CartSources);
    }
}
