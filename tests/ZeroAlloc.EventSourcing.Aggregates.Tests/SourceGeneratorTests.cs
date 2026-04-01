using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

// A partial aggregate — the generator emits ApplyEvent for this (no manual override)
public partial class ProductAggregate : Aggregate<ProductId, ProductState>
{
    public void Create(string name) => Raise(new ProductCreated(name));
    public void Discontinue() => Raise(new ProductDiscontinued());
    public void SetId(ProductId id) => Id = id;
}

public readonly record struct ProductId(Guid Value);

public record ProductCreated(string Name);
public record ProductDiscontinued();

public struct ProductState : IAggregateState<ProductState>
{
    public static ProductState Initial => default;
    public bool IsCreated { get; private set; }
    public bool IsDiscontinued { get; private set; }
    public string? Name { get; private set; }

    internal ProductState Apply(ProductCreated e) => this with { IsCreated = true, Name = e.Name };
    internal ProductState Apply(ProductDiscontinued _) => this with { IsDiscontinued = true };
}

public class SourceGeneratorTests
{
    [Fact]
    public void GeneratedApplyEvent_RoutesCorrectly()
    {
        var product = new ProductAggregate();
        product.Create("Widget");

        product.State.IsCreated.Should().BeTrue();
        product.State.Name.Should().Be("Widget");
        product.DequeueUncommitted().Length.Should().Be(1);
    }

    [Fact]
    public void GeneratedApplyEvent_SecondArm_RoutesCorrectly()
    {
        var product = new ProductAggregate();
        product.Create("Widget");
        product.Discontinue();

        product.State.IsDiscontinued.Should().BeTrue();
        product.DequeueUncommitted().Length.Should().Be(2);
    }

    [Fact]
    public void GeneratedApplyEvent_UnknownEvent_ReturnStateUnchanged()
    {
        var product = new ProductAggregate();
        product.ApplyHistoric(new object(), new StreamPosition(1));

        product.State.IsCreated.Should().BeFalse();
    }
}
