using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamPositionTests
{
    [Fact]
    public void Start_IsZero()
    {
        StreamPosition.Start.Value.Should().Be(0);
    }

    [Fact]
    public void End_IsMinusOne()
    {
        StreamPosition.End.Value.Should().Be(-1);
    }

    [Fact]
    public void StreamPosition_Next_IncreasesByOne()
    {
        var pos = new StreamPosition(5);
        pos.Next().Value.Should().Be(6);
    }
}
