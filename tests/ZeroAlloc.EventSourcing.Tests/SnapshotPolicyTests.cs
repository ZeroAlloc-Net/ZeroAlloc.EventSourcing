using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class SnapshotPolicyTests
{
    [Theory]
    [InlineData(10, null, 10, true)]    // no prior snapshot, 10 events → snapshot
    [InlineData(10, 5, 15, true)]       // last at 5, now 15 → 10 since → snapshot
    [InlineData(10, 5, 14, false)]      // last at 5, now 14 → only 9 since → no snapshot
    [InlineData(10, 10, 19, false)]     // last at 10, now 19 → 9 since → no snapshot
    [InlineData(10, 10, 20, true)]      // last at 10, now 20 → exactly 10 → snapshot
    public void EveryNEvents_ShouldSnapshot_ReturnsExpected(
        int n, int? lastSnapshot, int current, bool expected)
    {
        var policy = SnapshotPolicy.EveryNEvents(n);
        var last = lastSnapshot.HasValue ? new StreamPosition(lastSnapshot.Value) : (StreamPosition?)null;
        policy.ShouldSnapshot(new StreamPosition(current), last).Should().Be(expected);
    }

    [Fact]
    public void Always_ShouldSnapshot_AlwaysTrue()
    {
        SnapshotPolicy.Always.ShouldSnapshot(new StreamPosition(1), null).Should().BeTrue();
        SnapshotPolicy.Always.ShouldSnapshot(new StreamPosition(100), new StreamPosition(99)).Should().BeTrue();
    }

    [Fact]
    public void Never_ShouldSnapshot_AlwaysFalse()
    {
        SnapshotPolicy.Never.ShouldSnapshot(new StreamPosition(1), null).Should().BeFalse();
        SnapshotPolicy.Never.ShouldSnapshot(new StreamPosition(1000), new StreamPosition(1)).Should().BeFalse();
    }

    [Fact]
    public void EveryNEvents_NLessThanOne_ThrowsArgumentOutOfRangeException()
    {
        var act = () => SnapshotPolicy.EveryNEvents(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
