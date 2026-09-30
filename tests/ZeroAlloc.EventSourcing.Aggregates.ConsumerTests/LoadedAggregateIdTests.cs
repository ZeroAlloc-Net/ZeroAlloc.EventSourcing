using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Every load path sets <see cref="Aggregate{TId,TState}.Id"/> to the id it was asked to load,
/// for an aggregate that never sets its own id. Issue #401.
/// </summary>
public sealed class LoadedAggregateIdTests
{
    private static StreamId StreamFor(OrderId id) => new($"order-{id.Value:N}");

    private sealed record Setup(
        IEventStore EventStore,
        InMemorySnapshotStore<OrderState> Snapshots,
        AggregateRepository<Order, OrderId> Inner);

    private static Setup Build()
    {
        var eventStore = new EventStore(
            new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry());
        var inner = new AggregateRepository<Order, OrderId>(eventStore, static () => new Order(), StreamFor);
        return new Setup(eventStore, new InMemorySnapshotStore<OrderState>(), inner);
    }

    private static SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState> Decorate(
        Setup s, SnapshotLoadingStrategy strategy)
        => new(
            innerRepository: s.Inner,
            snapshotStore: s.Snapshots,
            strategy: strategy,
            restoreState: static (order, state, pos) => order.RestoreState(state, pos),
            eventStore: s.EventStore,
            streamIdFactory: StreamFor,
            aggregateFactory: static () => new Order(),
            snapshotPolicy: SnapshotPolicy.EveryNEvents(2),
            extractState: static order => order.State);

    /// <summary>Saves three events without ever setting the aggregate's own Id.</summary>
    private static async Task<OrderId> SeedAsync(IAggregateRepository<Order, OrderId> repository)
    {
        var id = new OrderId(Guid.NewGuid());
        using var order = new Order();
        order.Place("alice");
        order.AddItem(10m);
        order.AddItem(5m);
        (await repository.SaveAsync(order, id)).IsSuccess.Should().BeTrue();
        order.Id.Should().Be(default(OrderId), "saving does not assign an id");
        return id;
    }

    [Fact]
    public async Task AggregateRepository_Load_SetsId()
    {
        var s = Build();
        var id = await SeedAsync(s.Inner);

        using var loaded = (await s.Inner.LoadAsync(id)).Value;

        loaded.Id.Should().Be(id);
        loaded.Version.Value.Should().Be(3);
    }

    [Fact]
    public async Task AggregateRepository_LoadOfAnEmptyStream_SetsId()
    {
        var s = Build();
        var id = new OrderId(Guid.NewGuid());

        using var loaded = (await s.Inner.LoadAsync(id)).Value;

        loaded.Id.Should().Be(id);
        loaded.Version.Should().Be(StreamPosition.Start);
    }

    [Fact]
    public async Task AggregateRepository_Load_OverwritesAnIdTheFactorySet()
    {
        var s = Build();
        var id = await SeedAsync(s.Inner);
        var other = new OrderId(Guid.NewGuid());
        var repository = new AggregateRepository<Order, OrderId>(
            s.EventStore,
            () =>
            {
                var order = new Order();
                order.SetId(other);
                return order;
            },
            StreamFor);

        using var loaded = (await repository.LoadAsync(id)).Value;

        loaded.Id.Should().Be(id, "the aggregate holds the stream it was loaded from");
    }

    [Theory]
    [InlineData(SnapshotLoadingStrategy.TrustSnapshot)]
    [InlineData(SnapshotLoadingStrategy.ValidateAndReplay)]
    public async Task Decorator_LoadFromSnapshot_SetsId(SnapshotLoadingStrategy strategy)
    {
        var s = Build();
        var repository = Decorate(s, strategy);
        var id = await SeedAsync(repository);
        (await s.Snapshots.ReadAsync(StreamFor(id))).Should().NotBeNull();

        using var loaded = (await repository.LoadAsync(id)).Value;

        loaded.Id.Should().Be(id);
        loaded.Version.Value.Should().Be(3);
        loaded.State.Items.Should().Be(2);
    }

    [Theory]
    [InlineData(SnapshotLoadingStrategy.IgnoreSnapshot)]
    [InlineData(SnapshotLoadingStrategy.TrustSnapshot)]
    [InlineData(SnapshotLoadingStrategy.ValidateAndReplay)]
    public async Task Decorator_LoadWithoutSnapshot_SetsId(SnapshotLoadingStrategy strategy)
    {
        var s = Build();
        var id = await SeedAsync(s.Inner);
        (await s.Snapshots.ReadAsync(StreamFor(id))).Should().BeNull();

        using var loaded = (await Decorate(s, strategy).LoadAsync(id)).Value;

        loaded.Id.Should().Be(id);
        loaded.Version.Value.Should().Be(3);
    }

    [Fact]
    public async Task Decorator_ValidateAndReplay_FallbackToFullReplay_SetsId()
    {
        var s = Build();
        var id = await SeedAsync(s.Inner);
        using (var source = new Order())
        {
            source.Place("mallory");
            await s.Snapshots.WriteAsync(StreamFor(id), new StreamPosition(9), source.State);
        }

        using var loaded = (await Decorate(s, SnapshotLoadingStrategy.ValidateAndReplay).LoadAsync(id)).Value;

        loaded.Id.Should().Be(id);
        loaded.Version.Value.Should().Be(3, "the snapshot is past the stream head, so the load replayed");
    }
}
