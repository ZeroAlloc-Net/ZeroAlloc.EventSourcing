using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// The snapshot load path of <see cref="SnapshotCachingRepositoryDecorator{TAggregate,TId,TState}"/>
/// driven from an assembly without InternalsVisibleTo, through the public
/// <c>RestoreState</c> that the <c>restoreState</c> callback needs. Issue #387.
/// </summary>
public sealed class SnapshotRestoreTests
{
    private static StreamId StreamFor(OrderId id) => new($"order-{id.Value:N}");

    private sealed record Setup(
        ReadRecordingEventStore EventStore,
        InMemorySnapshotStore<OrderState> Snapshots,
        AggregateRepository<Order, OrderId> Inner,
        SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState> Repository);

    private static Setup Build(SnapshotLoadingStrategy strategy)
    {
        var eventStore = new ReadRecordingEventStore(
            new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry()));
        var snapshots = new InMemorySnapshotStore<OrderState>();
        var inner = new AggregateRepository<Order, OrderId>(eventStore, static () => new Order(), StreamFor);
        var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: inner,
            snapshotStore: snapshots,
            strategy: strategy,
            restoreState: static (order, state, pos) => order.RestoreState(state, pos),
            eventStore: eventStore,
            streamIdFactory: StreamFor,
            aggregateFactory: static () => new Order(),
            snapshotPolicy: SnapshotPolicy.EveryNEvents(2),
            extractState: static order => order.State);
        return new Setup(eventStore, snapshots, inner, repository);
    }

    /// <summary>Two saves: version 2 with a snapshot at 2, then version 3 with no new snapshot.</summary>
    private static async Task<OrderId> SeedSnapshotPlusOneTailEventAsync(Setup s)
    {
        var id = new OrderId(Guid.NewGuid());

        using (var order = new Order())
        {
            order.SetId(id);
            order.Place("alice");
            order.AddItem(10.50m);
            (await s.Repository.SaveAsync(order, id)).IsSuccess.Should().BeTrue();
        }

        var snapshot = await s.Snapshots.ReadAsync(StreamFor(id));
        snapshot.Should().NotBeNull();
        snapshot!.Value.Position.Value.Should().Be(2);

        // Loaded through the inner repository, so writing the tail event needs no snapshot load.
        using (var order = (await s.Inner.LoadAsync(id)).Value)
        {
            order.AddItem(4.25m);
            var saved = await s.Repository.SaveAsync(order, id);
            saved.IsSuccess.Should().BeTrue();
            saved.Value.NextExpectedVersion.Value.Should().Be(3);
        }

        var after = await s.Snapshots.ReadAsync(StreamFor(id));
        after!.Value.Position.Value.Should().Be(2, "3 - 2 is below EveryNEvents(2)");
        return id;
    }

    [Theory]
    [InlineData(SnapshotLoadingStrategy.TrustSnapshot)]
    [InlineData(SnapshotLoadingStrategy.ValidateAndReplay)]
    public async Task Load_FromSnapshotPlusTail_RestoresStateAndVersion_ThenAppendsAtTheRightExpectedVersion(
        SnapshotLoadingStrategy strategy)
    {
        var s = Build(strategy);
        var id = await SeedSnapshotPlusOneTailEventAsync(s);
        s.EventStore.ReadsFrom.Clear();

        var loaded = await s.Repository.LoadAsync(id);

        loaded.IsSuccess.Should().BeTrue();
        using var order = loaded.Value;

        // The load went through the snapshot: no read started at the beginning of the stream.
        s.EventStore.ReadsFrom.Should().NotBeEmpty().And.OnlyContain(p => p == new StreamPosition(2));

        order.State.IsPlaced.Should().BeTrue();
        order.State.Items.Should().Be(2);
        order.State.Total.Should().Be(14.75m);
        order.State.IsShipped.Should().BeFalse();
        order.Version.Value.Should().Be(3);
        order.OriginalVersion.Value.Should().Be(3);

        // Same state and versions as a full replay of the stream.
        using (var replayed = (await s.Inner.LoadAsync(id)).Value)
        {
            order.State.Should().Be(replayed.State);
            order.Version.Should().Be(replayed.Version);
            order.OriginalVersion.Should().Be(replayed.OriginalVersion);
        }

        order.Ship("TRACK-1");
        var saved = await s.Repository.SaveAsync(order, id);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.NextExpectedVersion.Value.Should().Be(4);
        order.OriginalVersion.Value.Should().Be(4);

        var positions = new List<long>();
        await foreach (var e in s.EventStore.ReadAsync(StreamFor(id), StreamPosition.Start))
            positions.Add(e.Position.Value);
        positions.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Load_FromSnapshot_HasNoUncommittedEvents()
    {
        var s = Build(SnapshotLoadingStrategy.TrustSnapshot);
        var id = await SeedSnapshotPlusOneTailEventAsync(s);

        using var order = (await s.Repository.LoadAsync(id)).Value;
        var saved = await s.Repository.SaveAsync(order, id);

        // Nothing to append: the save reports the loaded version and the stream is unchanged.
        saved.IsSuccess.Should().BeTrue();
        saved.Value.NextExpectedVersion.Value.Should().Be(3);
        var count = 0;
        await foreach (var _ in s.EventStore.ReadAsync(StreamFor(id), StreamPosition.Start))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Load_FromSnapshot_KeepsOptimisticConcurrency()
    {
        var s = Build(SnapshotLoadingStrategy.TrustSnapshot);
        var id = await SeedSnapshotPlusOneTailEventAsync(s);

        using var first = (await s.Repository.LoadAsync(id)).Value;
        using var second = (await s.Repository.LoadAsync(id)).Value;

        first.Ship("TRACK-1");
        (await s.Repository.SaveAsync(first, id)).IsSuccess.Should().BeTrue();

        second.AddItem(1m);
        var conflict = await s.Repository.SaveAsync(second, id);

        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task RestoreState_OnAnAggregateLoadedFromTheStore_Throws()
    {
        var s = Build(SnapshotLoadingStrategy.TrustSnapshot);
        var id = await SeedSnapshotPlusOneTailEventAsync(s);
        using var order = (await s.Repository.LoadAsync(id)).Value;
        var before = order.State;

        var act = () => order.RestoreState(OrderState.Initial, new StreamPosition(1));

        act.Should().Throw<InvalidOperationException>();
        order.State.Should().Be(before);
        order.Version.Value.Should().Be(3);
    }

    [Fact]
    public void RestoreState_AfterRaise_Throws()
    {
        using var order = new Order();
        order.Place("alice");

        var act = () => order.RestoreState(OrderState.Initial, new StreamPosition(5));

        act.Should().Throw<InvalidOperationException>();
        order.Version.Value.Should().Be(1);
    }

    [Fact]
    public void RestoreState_Twice_Throws()
    {
        using var source = new Order();
        source.Place("alice");
        using var order = new Order();
        order.RestoreState(source.State, new StreamPosition(1));

        var act = () => order.RestoreState(source.State, new StreamPosition(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Load_WithAFactoryThatReturnsAUsedAggregate_FailsInsteadOfMixingState()
    {
        // The decorator restores onto whatever aggregateFactory returns. A factory that hands out
        // an aggregate with events already on it is a bug; the guard surfaces it rather than
        // silently layering snapshot state over those events.
        var s = Build(SnapshotLoadingStrategy.TrustSnapshot);
        var id = await SeedSnapshotPlusOneTailEventAsync(s);
        using var used = new Order();
        used.Place("bob");
        var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            s.Inner,
            s.Snapshots,
            SnapshotLoadingStrategy.TrustSnapshot,
            static (order, state, pos) => order.RestoreState(state, pos),
            s.EventStore,
            StreamFor,
            () => used);

        var act = async () => await repository.LoadAsync(id);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
