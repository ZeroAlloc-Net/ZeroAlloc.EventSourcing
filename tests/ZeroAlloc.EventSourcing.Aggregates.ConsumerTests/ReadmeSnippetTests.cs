using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Kafka;
using ZeroAlloc.EventSourcing.Telemetry;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// The snippets from README.md, copied as they appear there and compiled against the public API,
/// using the Order domain from <c>Domain.cs</c>. Each test runs its snippet, except the Kafka one,
/// which needs a broker and is only compiled. When a snippet changes in the README, change it
/// here as well. See issue #402.
/// </summary>
public sealed class ReadmeSnippetTests
{
    private static IEventStore NewEventStore()
        => new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry());

    /// <summary>README.md, "Basic Event Sourcing".</summary>
    [Fact]
    public async Task QuickStart_AppendsAndReads()
    {
        IEventSerializer serializer = new JsonEventSerializer();
        var read = new List<object>();

        // --- snippet ---
        // The adapter is the storage: InMemoryEventStoreAdapter for tests; SqlServerEventStoreAdapter,
        // PostgreSqlEventStoreAdapter or SqliteEventStoreAdapter from their packages in production.
        var adapter = new InMemoryEventStoreAdapter();

        // serializer is your IEventSerializer; AddEventSourcing() registers the AOT-safe
        // ZeroAllocEventSerializer. OrderEventTypeRegistry maps event names to types: the source
        // generator emits it for an Order aggregate.
        var eventStore = new EventStore(adapter, serializer, new OrderEventTypeRegistry());

        // Append events
        var streamId = new StreamId("order-123");
        var appended = await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlaced("alice"), new ItemAdded(100m) },
            StreamPosition.Start);

        // Read events
        await foreach (var envelope in eventStore.ReadAsync(streamId))
        {
            Console.WriteLine($"Event {envelope.Position.Value}: {envelope.Event}");
            read.Add(envelope.Event);
        }
        // --- end snippet ---

        appended.IsSuccess.Should().BeTrue();
        read.Should().Equal(new OrderPlaced("alice"), new ItemAdded(100m));
    }

    /// <summary>README.md, "Stream Consumers", "Quick Start".</summary>
    [Fact]
    public async Task StreamConsumer_QuickStart()
    {
        var eventStore = NewEventStore();
        await eventStore.AppendAsync(new StreamId("order-1"), new object[] { new OrderPlaced("alice") }, StreamPosition.Start);
        var checkpointStore = new InMemoryCheckpointStore();

        // --- snippet ---
        var consumer = new StreamConsumer(eventStore, checkpointStore, "my-consumer");
        await consumer.ConsumeAsync((envelope, ct) =>
        {
            // Process event
            Console.WriteLine(envelope.Event);
            return Task.CompletedTask;
        });
        // --- end snippet ---

        (await consumer.GetPositionAsync()).Should().Be(new StreamPosition(1));
    }

    /// <summary>README.md, "Kafka Integration". Compiled, not run: it needs a Kafka broker.</summary>
    private static async Task KafkaIntegration(
        ICheckpointStore checkpointStore, IEventSerializer serializer, IEventTypeRegistry registry, EventHandler handler)
    {
        // --- snippet ---
        var options = new KafkaConsumerGroupOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "my-events",
            GroupId = "my-service",
            ConsumerId = "my-service-1"
        };

        using var consumer = new KafkaConsumerGroupConsumer(options, checkpointStore, serializer, registry);
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            // Process event from Kafka
            await handler.ProcessAsync(envelope, ct);
        });
        // --- end snippet ---
    }

    private sealed class EventHandler
    {
        public Task ProcessAsync(EventEnvelope envelope, CancellationToken ct) => Task.CompletedTask;
    }

    // --- snippet: README.md, "Projections" ---
    public sealed record OrderTotals(int Orders, decimal Revenue);

    public sealed class OrderTotalsProjection : Projection<OrderTotals>
    {
        public OrderTotalsProjection() => Current = new OrderTotals(0, 0m);

        protected override OrderTotals Apply(OrderTotals current, EventEnvelope @event) => @event.Event switch
        {
            OrderPlaced => current with { Orders = current.Orders + 1 },
            ItemAdded e => current with { Revenue = current.Revenue + e.Price },
            _ => current
        };
    }
    // --- end snippet ---

    /// <summary>README.md, "Projections".</summary>
    [Fact]
    public async Task Projections_TotalsOverTheGlobalStream()
    {
        var eventStore = NewEventStore();
        await eventStore.AppendAsync(new StreamId("order-1"), new object[] { new OrderPlaced("a"), new ItemAdded(3m) }, StreamPosition.Start);
        await eventStore.AppendAsync(new StreamId("order-2"), new object[] { new OrderPlaced("b"), new ItemAdded(4m) }, StreamPosition.Start);

        // --- snippet ---
        // Feed it every stream's events, in append order
        var projection = new OrderTotalsProjection();
        await foreach (var envelope in eventStore.ReadAsync(StreamId.Global))
        {
            await projection.HandleAsync(envelope);
        }
        // --- end snippet ---

        projection.Current.Should().Be(new OrderTotals(2, 7m));
    }

    /// <summary>README.md, "Snapshots".</summary>
    [Fact]
    public async Task Snapshots_WritesEveryNEventsAndLoads()
    {
        var eventStore = NewEventStore();
        var orderId = new OrderId(Guid.NewGuid());

        // --- snippet ---
        // Loads start from the latest snapshot and replay only the newer events; saves write a new
        // snapshot every 100 events
        var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: new AggregateRepository<Order, OrderId>(
                eventStore,
                () => new Order(),
                id => new StreamId($"order-{id.Value}")),
            snapshotStore: new InMemorySnapshotStore<OrderState>(),
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (order, state, position) => order.RestoreState(state, position),
            eventStore: eventStore,
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order(),
            snapshotPolicy: SnapshotPolicy.EveryNEvents(100),
            extractState: order => order.State);

        var loaded = await repository.LoadAsync(orderId);
        // --- end snippet ---

        loaded.IsSuccess.Should().BeTrue();

        using (var order = new Order())
        {
            order.Place("alice");
            for (var i = 0; i < 99; i++)
                order.AddItem(1m);
            (await repository.SaveAsync(order, orderId)).IsSuccess.Should().BeTrue();
        }

        var reloaded = await repository.LoadAsync(orderId);
        reloaded.IsSuccess.Should().BeTrue();
        using var result = reloaded.Value;
        result.State.Items.Should().Be(99);
        result.Version.Value.Should().Be(100);
    }

    /// <summary>README.md and docs/telemetry.md, "OpenTelemetry Instrumentation".</summary>
    [Fact]
    public void Telemetry_DecoratesTheAggregateRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventSerializer, JsonEventSerializer>();
        services.AddSingleton<IEventTypeRegistry, OrderEventTypeRegistry>();

        // --- snippet ---
        services
            .AddEventSourcing()
            .UseInMemoryEventStore()
            .UseAggregateRepository<Order, OrderId>(() => new Order(), id => new StreamId($"order-{id.Value}"))
            .WithTelemetry();   // call after the aggregate repository registrations, before BuildServiceProvider()
        // --- end snippet ---

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAggregateRepository<Order, OrderId>>()
            .Should().BeOfType<InstrumentedAggregateRepository<Order, OrderId>>();
    }
}
