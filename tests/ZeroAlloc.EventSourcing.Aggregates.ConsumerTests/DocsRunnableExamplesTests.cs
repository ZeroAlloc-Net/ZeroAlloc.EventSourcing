using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Examples.Advanced;
using ZeroAlloc.EventSourcing.Examples.GettingStarted;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Runs the examples under docs/examples, which this project compiles, and checks the state
/// each one ends with. The tests in docs/examples/03-testing run as ordinary tests.
/// </summary>
public sealed class DocsRunnableExamplesTests
{
    [Fact]
    public async Task CreateFirstAggregate_SavesAndShipsTheOrder()
    {
        var state = await CreateFirstAggregateExample.RunAsync();

        state.IsShipped.Should().BeTrue();
        state.Total.Should().Be(1500m);
        state.TrackingNumber.Should().Be("TRACK-ABC123");
    }

    [Fact]
    public async Task AppendAndRead_ReloadsAndSavesAgain()
    {
        var state = await AppendAndReadExample.RunAsync();

        state.QuantityOnHand.Should().Be(100);
        state.QuantityReserved.Should().Be(40);
        state.AvailableQuantity.Should().Be(60);
    }

    [Fact]
    public async Task CustomSnapshotStore_LoadsFromSnapshotAndMatchesTheStream()
    {
        var state = await CustomSnapshotStoreExample.RunAsync();

        // 6 deposits of 100 and 6 withdrawals of 50
        state.Balance.Should().Be(300m);
        state.TransactionCount.Should().Be(12);
    }
}
