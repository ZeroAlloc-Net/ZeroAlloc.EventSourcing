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

    [Fact]
    public void Global_is_singleton_with_star_value()
    {
        StreamId.Global.Value.Should().Be("*");
        StreamId.Global.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public void IsGlobal_true_for_star_streamId_constructed_from_string()
    {
        new StreamId("*").IsGlobal.Should().BeTrue();
    }

    [Fact]
    public void IsGlobal_false_for_legacy_dollar_all_alias()
    {
        // We deliberately did NOT promote "$all" as global — only "*". The InMemory
        // adapter's legacy "$all" recognition can stay as a back-compat read alias
        // but the canonical typed identity is "*".
        new StreamId("$all").IsGlobal.Should().BeFalse();
    }

    [Fact]
    public void IsGlobal_false_for_regular_streamIds()
    {
        new StreamId("order-1").IsGlobal.Should().BeFalse();
        new StreamId("customer-99").IsGlobal.Should().BeFalse();
    }
}
