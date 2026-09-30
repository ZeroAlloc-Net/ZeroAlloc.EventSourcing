using System.Collections.Immutable;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.CoreProjectionsDocs;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Runs the snippets of docs/core-concepts/projections.md, which
/// <c>CoreProjectionsDocSnippets.cs</c> holds. See issue #410.
/// </summary>
public sealed class CoreProjectionsDocTests
{
    private static readonly DateTimeOffset September = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static IEventStore NewEventStore()
        => new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new PageEventTypeRegistry());

    private static EventEnvelope Envelope(object @event, long position, DateTimeOffset? occurredAt = null)
        => new(
            new StreamId("orders"),
            new StreamPosition(position),
            @event,
            EventMetadata.New(@event.GetType().Name) with { OccurredAt = occurredAt ?? September });

    private static async Task FeedAsync<T>(Projection<T> projection, params object[] events)
    {
        for (var i = 0; i < events.Length; i++)
            await projection.HandleAsync(Envelope(events[i], i + 1));
    }

    private static async Task AppendAsync(IEventStore store, string stream, params object[] events)
        => (await store.AppendAsync(new StreamId(stream), events, StreamPosition.Start)).IsSuccess.Should().BeTrue();

    [Fact]
    public async Task SingleStream_BuildsTheOrderDetails()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1", new OrderPlacedEvent("1", "c", "Carol", 10m), new OrderShippedEvent("1", "T"));

        var details = await Usage.SingleStreamAsync(store, "1");

        details.Status.Should().Be("Shipped");
        details.TrackingNumber.Should().Be("T");
        details.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MultiStream_OneSummaryPerCustomer()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1", new OrderPlacedEvent("1", "alice", "Alice", 10m));
        await AppendAsync(store, "order-2", new OrderPlacedEvent("2", "bob", "Bob", 20m));
        await AppendAsync(store, "order-3", new OrderPlacedEvent("3", "alice", "Alice", 5m), new OrderCancelledEvent("3", "alice", 5m));

        var summaries = await Usage.MultiStreamAsync(store);

        summaries["alice"].Current.OrderCount.Should().Be(2);
        summaries["alice"].Current.TotalRevenue.Should().Be(10m);
        summaries["alice"].Current.RecentOrderIds.Should().Equal("3", "1");
        summaries["bob"].Current.TotalRevenue.Should().Be(20m);
    }

    [Fact]
    public async Task WaitForProjectionCatchUp_ReturnsOnceTheConsumerPassedTheWrite()
    {
        var store = NewEventStore();
        var checkpoints = new InMemoryCheckpointStore();
        var appended = await store.AppendAsync(new StreamId("order-1"), new object[] { new OrderPlacedEvent("1", "c", "C", 1m) }, StreamPosition.Start);
        var consumer = new StreamConsumer(store, checkpoints, "details", streamId: new StreamId("order-1"));
        await consumer.ConsumeAsync((_, _) => Task.CompletedTask);

        await Usage.WaitForProjectionCatchUp(checkpoints, "details", appended.Value.NextExpectedVersion, TimeSpan.FromSeconds(1));

        var waitForLater = () => Usage.WaitForProjectionCatchUp(checkpoints, "details", new StreamPosition(2), TimeSpan.FromMilliseconds(250)).AsTask();
        await waitForLater.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Materialized_UpsertsTheChangedRow()
    {
        var table = new RecordingTable();
        var projection = new OrdersMaterializationProjection(table);

        await FeedAsync(projection,
            new OrderPlacedEvent("1", "c", "C", 10m),
            new OrderPlacedEvent("2", "c", "C", 20m),
            new OrderShippedEvent("1", "T"),
            new OrderDeliveredEvent("1"));

        table.Upserts.Select(r => $"{r.OrderId}:{r.Status}").Should().Equal("1:Placed", "2:Placed", "1:Shipped");
    }

    [Fact]
    public async Task Revenue_ByTheMonthTheEventOccurredIn()
    {
        var projection = new RevenueProjection();

        await projection.HandleAsync(Envelope(new OrderPlacedEvent("1", "c", "C", 10m), 1, September));
        await projection.HandleAsync(Envelope(new OrderPlacedEvent("2", "c", "C", 5m), 2, September.AddMonths(1)));
        await projection.HandleAsync(Envelope(new OrderCancelledEvent("1", "c", 10m), 3, September.AddMonths(1)));

        projection.Current.ByMonth.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["2026-09"] = 10m, ["2026-10"] = -5m });
    }

    [Fact]
    public async Task SearchIndex_FindsByKeyword()
    {
        var searchIndex = new OrderSearchIndexProjection();
        await FeedAsync(searchIndex, new OrderPlacedEvent("1", "c", "Customer Name", 10m), new OrderPlacedEvent("2", "d", "Other", 1m));

        searchIndex.Current.Search("customer name").Select(d => d.OrderId).Should().Equal("1");
    }

    [Fact]
    public async Task Rebuild_WithoutDowntime_SwapsWhenDone()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1", new OrderPlacedEvent("1", "c", "C", 10m), new OrderShippedEvent("1", "T"));
        var service = new OrderQueryService();
        service.Current.Status.Should().Be("New");

        await service.RebuildAsync(store, new StreamId("order-1"));

        service.Current.Should().Be(new OrderData("1", "Shipped", 10m, "T"));
    }

    [Fact]
    public async Task Idempotent_AndDeduplicating()
    {
        var idempotent = new IdempotentOrderProjection();
        var tracking = new TrackingProjection();
        var placed = Envelope(new OrderPlacedEvent("1", "c", "C", 10m), 1);

        await idempotent.HandleAsync(placed);
        await idempotent.HandleAsync(placed);
        await tracking.HandleAsync(placed);
        await tracking.HandleAsync(placed);

        idempotent.Current.Total.Should().Be(10m);
        tracking.Current.Data.Total.Should().Be(10m);
        tracking.Current.LastPosition.Should().Be(new StreamPosition(1));
    }

    [Fact]
    public async Task Dashboard_UpdatesFromTheSubscription()
    {
        var store = NewEventStore();
        var stream = new StreamId("order-1");

        var dashboard = await Dashboard.SubscribeAsync(store, stream, () =>
            AppendAsync(store, "order-1", new OrderPlacedEvent("1", "c", "C", 10m), new OrderShippedEvent("1", "T")));

        dashboard.Current.Should().Be(new OrderMetrics(1, 1, 10m));
    }

    [Fact]
    public async Task Notifications_ForShippedAndDelivered()
    {
        var service = new RecordingNotifications();
        var handler = new OrderNotificationHandler(service);

        await handler.HandleAsync(Envelope(new OrderShippedEvent("1", "T"), 1), CancellationToken.None);
        await handler.HandleAsync(Envelope(new OrderPlacedEvent("2", "c", "C", 1m), 2), CancellationToken.None);
        await handler.HandleAsync(Envelope(new OrderDeliveredEvent("1"), 3), CancellationToken.None);

        service.Sent.Select(n => n.Message).Should().Equal("Order 1 shipped with T", "Order 1 delivered");
    }

    [Fact]
    public async Task Graph_AndQueries()
    {
        var graph = new CustomerOrderGraphProjection();
        await FeedAsync(graph, new OrderPlacedEvent("1", "alice", "A", 1m), new OrderPlacedEvent("2", "alice", "A", 1m));
        graph.Current["alice"].Should().Equal("1", "2");

        var eventStore = new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new OrderEventTypeRegistry());
        var repository = new AggregateRepository<Order, OrderId>(eventStore, () => new Order(), id => new StreamId($"order-{id.Value}"));
        var orderId = new OrderId(Guid.NewGuid());
        using (var order = new Order())
        {
            order.Place("alice");
            (await repository.SaveAsync(order, orderId)).IsSuccess.Should().BeTrue();
        }
        var list = new OrderListProjection();
        await FeedAsync(list, new OrderPlacedEvent("1", "alice", "A", 3m), new OrderShippedEvent("1", "T"));
        var summary = new CustomerSummaryProjection("alice");
        await FeedAsync(summary, new OrderPlacedEvent("1", "alice", "A", 3m));

        var (shipped, revenue) = await Queries.UseBothAsync(repository, orderId, list, summary);

        shipped.Should().Be(1);
        revenue.Should().Be(3m);
    }

    private sealed class RecordingTable : IOrdersTable
    {
        public List<OrdersDataStore> Upserts { get; } = [];

        public ValueTask UpsertAsync(OrdersDataStore row, CancellationToken ct)
        {
            Upserts.Add(row);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<Notification> Sent { get; } = [];

        public Task SendAsync(Notification notification, CancellationToken ct)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class PageEventTypeRegistry : IEventTypeRegistry
    {
        private static readonly ImmutableDictionary<string, Type> Types = new[]
        {
            typeof(OrderPlacedEvent), typeof(OrderShippedEvent), typeof(OrderDeliveredEvent), typeof(OrderCancelledEvent),
        }.ToImmutableDictionary(t => t.Name);

        public bool TryGetType(string eventType, out Type? type) => Types.TryGetValue(eventType, out type);

        public string GetTypeName(Type type) => type.Name;
    }
}
