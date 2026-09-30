using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.CustomProjectionsDocs;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Runs the snippets of docs/advanced/custom-projections.md, which
/// <c>CustomProjectionsDocSnippets.cs</c> holds. See issue #410.
/// </summary>
public sealed class CustomProjectionsDocTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static OrderPlacedEvent Placed(string orderId, string customerId, decimal total, params OrderLine[] lines)
        => new(orderId, customerId, total, lines);

    private static EventEnvelope Envelope(object @event, long position, DateTimeOffset? occurredAt = null)
        => new(
            new StreamId("orders"),
            new StreamPosition(position),
            @event,
            EventMetadata.New(@event.GetType().Name) with { OccurredAt = occurredAt ?? Monday });

    private static async Task FeedAsync<T>(Projection<T> projection, params object[] events)
    {
        for (var i = 0; i < events.Length; i++)
            await projection.HandleAsync(Envelope(events[i], i + 1));
    }

    private static IEventStore NewEventStore()
        => new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new PageEventTypeRegistry());

    [Fact]
    public async Task Filtered_KeepsOnlyHighValueOrders()
    {
        var projection = new HighValueOrdersProjection();

        await FeedAsync(projection,
            Placed("1", "c", 50_000m),
            Placed("2", "c", 10m),
            Placed("3", "c", 20_000m),
            new OrderCancelledEvent("3"));

        projection.Current.Keys.Should().Equal("1");
        projection.GetHighValueOrder("2").Should().BeNull();
    }

    [Fact]
    public async Task Composite_UpdatesAllModels()
    {
        // --- snippet: "Testing Projections" ---
        var projection = new OrderCompositeProjection();

        var envelope = new EventEnvelope(
            new StreamId("order-123"),
            new StreamPosition(1),
            new OrderPlacedEvent("order-123", "customer-1", 1000m, []),
            EventMetadata.New(nameof(OrderPlacedEvent)));

        await projection.HandleAsync(envelope);

        Assert.Equal(1, projection.GetOrderCount("customer-1"));
        Assert.Equal(1000m, projection.GetTotalRevenue("customer-1"));
        // --- end snippet ---

        projection.GetRecentOrders().Should().ContainSingle().Which.OrderId.Should().Be("order-123");
    }

    [Fact]
    public async Task Composite_KeepsTheLast100Orders()
    {
        var projection = new OrderCompositeProjection();

        await FeedAsync(projection, Enumerable.Range(1, 105).Select(i => (object)Placed($"{i}", "c", 1m)).ToArray());

        projection.GetRecentOrders().Should().HaveCount(100);
        projection.GetRecentOrders()[0].OrderId.Should().Be("6");
        projection.GetOrderCount("c").Should().Be(105);
    }

    [Fact]
    public async Task Stateful_TracksStock()
    {
        var projection = new InventoryProjection();

        await FeedAsync(projection,
            new StockReceivedEvent("p", 10),
            new StockReservedEvent("p", 4),
            new StockReleasedEvent("p", 1),
            new DamagedStockEvent("p", 2));

        projection.GetInventory("p").Should().Be(new InventoryState(OnHand: 8, Reserved: 3, Damaged: 2));
        projection.GetInventory("p")!.Available.Should().Be(5);
        projection.CanReserve("p", 5).Should().BeTrue();
        projection.CanReserve("p", 6).Should().BeFalse();
        projection.CanReserve("unknown", 1).Should().BeFalse();
    }

    [Fact]
    public async Task Denormalized_OneRowPerLineItem()
    {
        var projection = new OrderLineItemProjection();

        await FeedAsync(projection,
            Placed("1", "c", 0m, new OrderLine("p1", 2, 5m), new OrderLine("p2", 1, 3m)),
            Placed("2", "c", 0m, new OrderLine("p1", 1, 5m)),
            new OrderShippedEvent("1", "a@b.c", "T"));

        projection.GetProductRevenue("p1").Should().Be(15m);
        projection.GetOrderLineItems("1").Should().OnlyContain(li => li.OrderStatus == "Shipped");
        projection.GetOrderLineItems("2").Should().OnlyContain(li => li.OrderStatus == "Placed");
    }

    [Fact]
    public async Task Batched_WritesPerBatchAndOnFlush()
    {
        var eventStore = NewEventStore();
        var projectionStore = new InMemoryProjectionStore();
        await eventStore.AppendAsync(
            new StreamId("orders"),
            Enumerable.Range(1, 150).Select(i => (object)Placed($"{i}", "c", i)).ToArray(),
            StreamPosition.Start);

        await CustomProjectionsUsage.BatchedUsage(eventStore, projectionStore);

        var saved = await projectionStore.LoadAsync("order-totals");
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(saved!)!.Should().HaveCount(150);
    }

    [Fact]
    public async Task Composition_ReadsTheOtherProjections()
    {
        var orders = new OrderCompositeProjection();
        var inventory = new InventoryProjection();
        await FeedAsync(orders, Placed("1", "c", 7m));
        await FeedAsync(inventory, new StockReceivedEvent("p", 3));

        new DashboardProjection(orders, inventory).GetDashboard("c", "p")
            .Should().Be(new DashboardProjection.DashboardData(1, 7m, 3));
    }

    [Fact]
    public async Task SideEffects_RunFromAStreamConsumer()
    {
        var eventStore = NewEventStore();
        await eventStore.AppendAsync(new StreamId("orders"), new object[] { new OrderShippedEvent("1", "a@b.c", "T1") }, StreamPosition.Start);
        var email = new RecordingEmail();

        await CustomProjectionsUsage.SideEffectWiring(
            eventStore, new InMemoryCheckpointStore(), new OrderShippingNotificationHandler(email), CancellationToken.None);

        email.Sent.Should().Equal("a@b.c: Your order 1 has shipped");
    }

    [Fact]
    public async Task TimeWindowed_GroupsByTheWeekTheEventOccurredIn()
    {
        var projection = new WeeklyOrdersProjection();

        await projection.HandleAsync(Envelope(Placed("1", "c", 5m), 1, Monday));
        await projection.HandleAsync(Envelope(Placed("2", "c", 7m), 2, Monday.AddDays(3)));
        await projection.HandleAsync(Envelope(Placed("3", "c", 9m), 3, Monday.AddDays(7)));

        projection.GetWeekRevenue(Monday).Should().Be(12m);
        projection.GetWeekRevenue(Monday.AddDays(7)).Should().Be(9m);
    }

    [Fact]
    public async Task Persistent_ResumesAfterTheSavedPosition()
    {
        var eventStore = NewEventStore();
        var projectionStore = new InMemoryProjectionStore();
        var stream = new StreamId("orders");
        await eventStore.AppendAsync(stream, new object[] { Placed("1", "c", 1m), Placed("2", "c", 2m) }, StreamPosition.Start);
        await CustomProjectionsUsage.PersistentUsage(eventStore, projectionStore);

        await eventStore.AppendAsync(stream, new object[] { Placed("3", "c", 3m) }, new StreamPosition(2));
        var restarted = new PersistentOrderTotalsProjection(projectionStore);
        var resumeAfter = await restarted.LoadAsync();
        var replayed = 0;
        await foreach (var envelope in eventStore.ReadAsync(stream, resumeAfter))
        {
            await restarted.HandleAsync(envelope);
            replayed++;
        }

        resumeAfter.Should().Be(new StreamPosition(2));
        replayed.Should().Be(1);
        restarted.Current.Totals.Should().HaveCount(3);
        restarted.Current.LastPosition.Should().Be(3);
    }

    [Fact]
    public async Task Resilient_SkipsBadEventsAndStopsOnOthers()
    {
        var projection = new ResilientOrderTotalsProjection(NullLogger.Instance);

        await FeedAsync(projection, Placed("1", "c", 5m), Placed("2", "c", -1m), Placed("3", "c", 7m));

        projection.Current.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["1"] = 5m, ["3"] = 7m });
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> Sent { get; } = [];

        public Task SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            Sent.Add($"{to}: {subject}");
            return Task.CompletedTask;
        }
    }

    private sealed class PageEventTypeRegistry : IEventTypeRegistry
    {
        private static readonly ImmutableDictionary<string, Type> Types = new[]
        {
            typeof(OrderPlacedEvent), typeof(OrderShippedEvent), typeof(OrderCancelledEvent),
        }.ToImmutableDictionary(t => t.Name);

        public bool TryGetType(string eventType, out Type? type) => Types.TryGetValue(eventType, out type);

        public string GetTypeName(Type type) => type.Name;
    }
}
