using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamIdTests
{
    [Fact]
    public void TwoStreamIds_WithSameValue_AreEqual()
    {
        var a = new StreamId("orders-1");
        var b = new StreamId("orders-1");
        a.Should().Be(b);
    }

    [Fact]
    public void TwoStreamIds_WithDifferentValues_AreNotEqual()
    {
        var a = new StreamId("orders-1");
        var b = new StreamId("orders-2");
        a.Should().NotBe(b);
    }

    [Fact]
    public void StreamId_ToString_ReturnsValue()
    {
        var id = new StreamId("orders-1");
        id.ToString().Should().Be("orders-1");
    }
}
