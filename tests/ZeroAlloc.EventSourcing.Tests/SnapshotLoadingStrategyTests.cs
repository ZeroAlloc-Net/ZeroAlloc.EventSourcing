using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class SnapshotLoadingStrategyTests
{
    [Fact]
    public void TrustSnapshot_HasValue_Zero()
    {
        ((int)SnapshotLoadingStrategy.TrustSnapshot).Should().Be(0);
    }

    [Fact]
    public void ValidateAndReplay_HasValue_One()
    {
        ((int)SnapshotLoadingStrategy.ValidateAndReplay).Should().Be(1);
    }

    [Fact]
    public void IgnoreSnapshot_HasValue_Two()
    {
        ((int)SnapshotLoadingStrategy.IgnoreSnapshot).Should().Be(2);
    }

    [Fact]
    public void AllStrategies_AreInValidRange()
    {
        var strategies = new[]
        {
            SnapshotLoadingStrategy.TrustSnapshot,
            SnapshotLoadingStrategy.ValidateAndReplay,
            SnapshotLoadingStrategy.IgnoreSnapshot
        };

        foreach (var strategy in strategies)
        {
            var value = (int)strategy;
            value.Should().BeGreaterThanOrEqualTo(0);
            value.Should().BeLessThanOrEqualTo(2);
        }
    }
}
