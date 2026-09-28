using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// The decorator snippets from the docs, copied as they appear there and compiled against the
/// public API. Each test then loads an order from a snapshot through the snippet's repository, so
/// a snippet whose restoreState callback does nothing fails too. When a snippet changes in the
/// docs, change it here as well.
/// </summary>
public sealed class DocsExamplesTests
{
    private static async Task<OrderId> SeedAsync(IEventStore eventStore, ISnapshotStore<OrderState> snapshotStore)
    {
        var orderId = new OrderId(Guid.NewGuid());
        var streamId = new StreamId($"order-{orderId.Value}");

        var appended = await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlaced("alice"), new ItemAdded(3m), new ItemAdded(4m) }.AsMemory(),
            StreamPosition.Start);
        appended.IsSuccess.Should().BeTrue();

        // A snapshot after the first two events whose Total differs from the stream: 100 instead
        // of 3. A load that really restores it ends at 104; one that ignores it ends at 7.
        using var source = new Order();
        source.Place("alice");
        source.AddItem(100m);
        await snapshotStore.WriteAsync(streamId, new StreamPosition(2), source.State);
        return orderId;
    }

    private static async Task AssertLoadedFromSnapshotAsync(IAggregateRepository<Order, OrderId> repository, OrderId orderId)
    {
        var result = await repository.LoadAsync(orderId);

        result.IsSuccess.Should().BeTrue();
        using var order = result.Value;
        order.State.Total.Should().Be(104m);
        order.State.Items.Should().Be(2);
        order.Version.Value.Should().Be(3);
        order.OriginalVersion.Value.Should().Be(3);
    }

    private static IEventStore NewEventStore()
        => new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry());

    private static ServiceProvider NewServiceProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventSerializer, JsonEventSerializer>();
        services.AddSingleton<IEventTypeRegistry, OrderEventTypeRegistry>();
        services.AddEventSourcing()
            .UseInMemoryEventStore()
            .UseInMemorySnapshotStore<OrderState>();
        register(services);
        return services.BuildServiceProvider();
    }

    /// <summary>docs/core-concepts/snapshots.md, "Snapshot Caching Patterns".</summary>
    [Fact]
    public async Task CoreConcepts_Snapshots_CachingPattern()
    {
        var eventStore = NewEventStore();
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var orderId = await SeedAsync(eventStore, snapshotStore);

        // --- snippet ---
        // Standard repository
        var baseRepository = new AggregateRepository<Order, OrderId>(
            eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}"));

        // Wrap with snapshot caching
        var cachedRepository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: baseRepository,
            snapshotStore: snapshotStore,
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (order, state, pos) => order.RestoreState(state, pos),
            eventStore: eventStore,
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order());
        // --- end snippet ---

        await AssertLoadedFromSnapshotAsync(cachedRepository, orderId);
    }

    /// <summary>
    /// docs/examples/SnapshotOptimizedLoading.md, "Basic Usage", and
    /// docs/usage-guides/snapshots-usage.md, "Using SnapshotCachingRepositoryDecorator".
    /// </summary>
    [Fact]
    public async Task SnapshotOptimizedLoading_BasicUsage()
    {
        var eventStore = NewEventStore();
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var orderId = await SeedAsync(eventStore, snapshotStore);

        // --- snippet ---
        // Create inner repository (no snapshot logic)
        var innerRepo = new AggregateRepository<Order, OrderId>(
            eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}"));

        // Wrap with snapshot decorator
        var snapshotRepo = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: innerRepo,
            snapshotStore: snapshotStore,
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (order, state, pos) => order.RestoreState(state, pos),
            eventStore: eventStore,
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order());
        // --- end snippet ---

        await AssertLoadedFromSnapshotAsync(snapshotRepo, orderId);
    }

    /// <summary>
    /// docs/examples/SnapshotOptimizedLoading.md, "Configuration in DI Container", and
    /// docs/examples/SqlSnapshotStores.md, "With SnapshotCachingRepositoryDecorator".
    /// </summary>
    [Fact]
    public async Task SnapshotOptimizedLoading_DiContainer()
    {
        await using var provider = NewServiceProvider(services =>
        {
            // --- snippet ---
            services
                .AddScoped<IAggregateRepository<Order, OrderId>>(sp =>
                    new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
                        innerRepository: new AggregateRepository<Order, OrderId>(
                            sp.GetRequiredService<IEventStore>(),
                            () => new Order(),
                            id => new StreamId($"order-{id.Value}")),
                        snapshotStore: sp.GetRequiredService<ISnapshotStore<OrderState>>(),
                        strategy: SnapshotLoadingStrategy.ValidateAndReplay,
                        restoreState: (o, state, pos) => o.RestoreState(state, pos),
                        eventStore: sp.GetRequiredService<IEventStore>(),
                        streamIdFactory: id => new StreamId($"order-{id.Value}"),
                        aggregateFactory: () => new Order()));
            // --- end snippet ---
        });

        var orderId = await SeedAsync(
            provider.GetRequiredService<IEventStore>(),
            provider.GetRequiredService<ISnapshotStore<OrderState>>());

        await using var scope = provider.CreateAsyncScope();
        await AssertLoadedFromSnapshotAsync(
            scope.ServiceProvider.GetRequiredService<IAggregateRepository<Order, OrderId>>(),
            orderId);
    }
}
