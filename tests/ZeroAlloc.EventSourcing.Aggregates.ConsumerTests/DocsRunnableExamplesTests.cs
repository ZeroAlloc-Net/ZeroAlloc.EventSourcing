using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Examples.Advanced;
using ZeroAlloc.EventSourcing.Examples.GettingStarted;
using ZeroAlloc.EventSourcing.Examples.StreamConsumers;
using DomainModeling = ZeroAlloc.EventSourcing.Examples.DomainModeling;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Runs the examples under docs/examples, which this project compiles, and checks the state
/// each one ends with. The tests in docs/examples/03-testing run as ordinary tests. The
/// domain-modeling example has no entry point, so its test drives the aggregate itself.
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
    [Fact]
    public async Task DomainModeling_OrderGoesThroughItsLifecycleAndReloads()
    {
        var eventStore = new EventStore(
            new ZeroAlloc.EventSourcing.InMemory.InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new DomainModeling.OrderEventTypeRegistry());
        var repository = new ZeroAlloc.EventSourcing.Aggregates.AggregateRepository<DomainModeling.Order, DomainModeling.OrderId>(
            eventStore,
            () => new DomainModeling.Order(),
            id => new StreamId($"order-{id.Value}"));
        var orderId = new DomainModeling.OrderId(Guid.NewGuid());
        var lineItems = new[]
        {
            new DomainModeling.LineItem(new DomainModeling.ProductId(Guid.NewGuid()), 2, 10m),
            new DomainModeling.LineItem(new DomainModeling.ProductId(Guid.NewGuid()), 1, 5m),
        };

        using (var order = new DomainModeling.Order())
        {
            order.Place("ORD-1", new DomainModeling.CustomerId(Guid.NewGuid()), lineItems);
            order.Confirm();
            order.ProcessPayment(25m);
            order.Ship("TRACK-1");
            (await repository.SaveAsync(order, orderId)).IsSuccess.Should().BeTrue();
        }

        var loaded = await repository.LoadAsync(orderId);

        loaded.IsSuccess.Should().BeTrue();
        using var reloaded = loaded.Value;
        reloaded.State.Status.Should().Be("Shipped");
        reloaded.State.Total.Should().Be(25m);
        reloaded.State.LineItemCount.Should().Be(2);
        reloaded.State.TrackingNumber.Should().Be("TRACK-1");
    }

    [Fact]
    public void DomainModeling_CancelAfterPayment_IsAllowed_ButNotAfterShipping()
    {
        using var order = new DomainModeling.Order();
        order.Place("ORD-2", new DomainModeling.CustomerId(Guid.NewGuid()),
            [new DomainModeling.LineItem(new DomainModeling.ProductId(Guid.NewGuid()), 1, 10m)]);
        order.Confirm();
        order.ProcessPayment(10m);

        order.Cancel();

        order.State.Status.Should().Be("Cancelled");
        FluentActions.Invoking(order.Cancel).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task CustomProjection_BuildsBothReadModels()
    {
        var projection = await CustomProjectionExample.RunAsync();

        projection.GetCustomer("cust-1")!.IsActive.Should().BeFalse();
        projection.GetActiveCustomers().Select(c => c.CustomerId).Should().Equal("cust-2", "cust-3");
        projection.GetCustomersOnPlan("pro").Select(c => c.CustomerId).Should().Equal("cust-2");
        projection.GetSubscriptionsForPlan("basic").Should().BeEmpty();
        projection.GetActiveSubscription("cust-3")!.PlanId.Should().Be("enterprise");
        projection.GetStats().Should().Be((3, 2, 8));
    }

    [Fact]
    public async Task CustomEventStore_AppendsReadsDetectsConflictsAndNotifies()
    {
        var outcome = await CustomEventStoreExample.RunAsync();

        outcome.Events.Select(e => e.Event).Should().Equal(
            new AccountOpened("alice"), new FundsDeposited(100m), new FundsDeposited(25m));
        outcome.Events.Select(e => e.Position.Value).Should().Equal(1L, 2L, 3L);
        outcome.ConflictError!.Code.Should().Be("CONFLICT");
        outcome.Delivered.Should().Equal(new FundsDeposited(25m));
        outcome.StreamCount.Should().Be(1);
    }

    [Fact]
    public async Task StreamConsumer_ProcessesEveryStreamThenResets()
    {
        var outcome = await StreamConsumerExample.RunAsync();

        outcome.Processed.Should().Equal("kitchen:21.5", "kitchen:22", "garage:12");
        outcome.PositionAfterRun.Should().Be(new StreamPosition(3));
        outcome.PositionAfterReset.Should().BeNull();
    }
}
