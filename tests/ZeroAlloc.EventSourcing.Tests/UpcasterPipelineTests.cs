using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class UpcasterPipelineTests
{
    private record V1(string Id);
    private record V2(string Id, string Name);
    private record V3(string Id, string Name, int Version);

    [Fact]
    public void TryUpcast_ReturnsFalse_WhenNoUpcasterRegistered()
    {
        var pipeline = new UpcasterPipeline([]);
        var result = pipeline.TryUpcast(new V1("x"), out var upgraded);
        result.Should().BeFalse();
        upgraded.Should().BeEquivalentTo(new V1("x"));
    }

    [Fact]
    public void TryUpcast_AppliesSingleHop()
    {
        var reg = new UpcasterRegistration(typeof(V1), typeof(V2),
            o => { var v = (V1)o; return new V2(v.Id, "default"); });
        var pipeline = new UpcasterPipeline([reg]);

        var result = pipeline.TryUpcast(new V1("abc"), out var upgraded);

        result.Should().BeTrue();
        upgraded.Should().BeEquivalentTo(new V2("abc", "default"));
    }

    [Fact]
    public void TryUpcast_ChainsMultipleHops()
    {
        var reg1 = new UpcasterRegistration(typeof(V1), typeof(V2),
            o => { var v = (V1)o; return new V2(v.Id, "default"); });
        var reg2 = new UpcasterRegistration(typeof(V2), typeof(V3),
            o => { var v = (V2)o; return new V3(v.Id, v.Name, 3); });
        var pipeline = new UpcasterPipeline([reg1, reg2]);

        var result = pipeline.TryUpcast(new V1("abc"), out var upgraded);

        result.Should().BeTrue();
        upgraded.Should().BeEquivalentTo(new V3("abc", "default", 3));
    }

    [Fact]
    public void TryUpcast_ThrowsOnExcessiveDepth()
    {
        // Create a cycle: V1 -> V2 -> V1
        var regAToB = new UpcasterRegistration(typeof(V1), typeof(V2),
            o => new V2(((V1)o).Id, "x"));
        var regBToA = new UpcasterRegistration(typeof(V2), typeof(V1),
            o => new V1(((V2)o).Id));
        var pipeline = new UpcasterPipeline([regAToB, regBToA]);

        var act = () => pipeline.TryUpcast(new V1("x"), out _);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exceeded maximum depth*");
    }
}
