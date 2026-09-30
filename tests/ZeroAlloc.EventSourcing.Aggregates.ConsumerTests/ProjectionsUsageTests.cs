using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.ProjectionsUsage;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Runs the snippets of docs/usage-guides/projections-usage.md, which
/// <c>ProjectionsUsageSnippets.cs</c> holds, against a real event store.
/// </summary>
public sealed class ProjectionsUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static IEventStore NewEventStore()
        => new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new GuideEventTypeRegistry());

    private static async Task AppendAsync(IEventStore store, string stream, params object[] events)
    {
        var result = await store.AppendAsync(new StreamId(stream), events.AsMemory(), StreamPosition.Start);
        result.IsSuccess.Should().BeTrue();
    }

    private static async Task<TState> ReplayAsync<TState>(IEventStore store, StreamId stream, Projection<TState> projection)
    {
        // --- snippet: usage ---
        await foreach (var envelope in store.ReadAsync(stream, StreamPosition.Start))
        {
            await projection.HandleAsync(envelope);
        }
        // --- end snippet ---
        return projection.Current;
    }

    private static EventEnvelope Envelope(object @event, long position)
        => new(new StreamId("order-1"), new StreamPosition(position), @event, EventMetadata.New(@event.GetType().Name));

    [Fact]
    public async Task SingleStream_OrderDetails()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1",
            new OrderPlacedEvent("1", "alice", 10m, T0),
            new OrderConfirmedEvent("1", T0.AddMinutes(1)),
            new OrderShippedEvent("1", "TRACK", T0.AddMinutes(2)));

        var details = await ReplayAsync(store, new StreamId("order-1"), new OrderDetailsProjection());

        details.Should().Be(new OrderDetails("1", "alice", 10m, "Shipped", "TRACK", T0, T0.AddMinutes(1), T0.AddMinutes(2)));
    }

    [Fact]
    public async Task MultiStream_CustomerSummary_ReadsTheGlobalStream()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1", new OrderPlacedEvent("1", "alice", 10m, T0));
        await AppendAsync(store, "order-2", new OrderPlacedEvent("2", "bob", 99m, T0));
        await AppendAsync(store, "order-3",
            new OrderPlacedEvent("3", "alice", 5m, T0),
            new OrderRefundedEvent("3", "alice", 2m));

        var summary = await ReplayAsync(store, StreamId.Global, new CustomerOrderSummaryProjection("alice"));

        summary.OrderCount.Should().Be(2);
        summary.TotalRevenue.Should().Be(13m);
        summary.RecentOrderIds.Should().Equal("3", "1");
    }

    [Fact]
    public async Task Materialization_SavesOnlyWhenTheReadModelChanges()
    {
        var repository = new RecordingRepository();
        var projection = new MaterializedOrderDetailsProjection(repository);

        await projection.HandleAsync(Envelope(new OrderPlacedEvent("1", "alice", 10m, T0), 1));
        await projection.HandleAsync(Envelope(new OrderDeliveredEvent("1"), 2));

        repository.Saved.Should().ContainSingle().Which.Status.Should().Be("Placed");
        (await repository.GetByIdAsync("1"))!.Total.Should().Be(10m);
    }

    [Fact]
    public async Task Counts_PerCustomer()
    {
        var projection = new CustomerOrderCountProjection();

        await projection.HandleAsync(Envelope(new OrderPlacedEvent("1", "alice", 1m, T0), 1));
        await projection.HandleAsync(Envelope(new OrderPlacedEvent("2", "alice", 1m, T0), 2));
        await projection.HandleAsync(Envelope(new OrderCancelledEvent("2", "alice"), 3));
        await projection.HandleAsync(Envelope(new OrderCancelledEvent("9", "carol"), 4));

        projection.Current.Should().BeEquivalentTo(new Dictionary<string, int> { ["alice"] = 1 });
    }

    [Fact]
    public async Task SearchIndex_ByCustomerAndStatus()
    {
        var projection = new OrderSearchProjection();
        await projection.HandleAsync(Envelope(new OrderPlacedEvent("1", "alice", 1m, T0), 1));
        await projection.HandleAsync(Envelope(new OrderPlacedEvent("2", "bob", 2m, T0), 2));
        await projection.HandleAsync(Envelope(new OrderShippedEvent("2", "T", T0), 3));

        var search = new OrderSearchService(projection);

        search.SearchByCustomer("alice").Select(o => o.OrderId).Should().Equal("1");
        search.SearchByStatus("Shipped").Select(o => o.OrderId).Should().Equal("2");
    }

    [Fact]
    public async Task Notifications_RunFromAStreamConsumer()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1",
            new OrderPlacedEvent("1", "alice", 10m, T0),
            new OrderShippedEvent("1", "TRACK", T0));
        var email = new RecordingEmail();
        var slack = new RecordingSlack();
        var handler = new OrderNotificationHandler(email, slack);

        // --- snippet: "Pattern 4" wiring ---
        // A StreamConsumer checkpoints what it handled, so a restart does not send twice
        var consumer = new StreamConsumer(store, new InMemoryCheckpointStore(), consumerId: "order-notifications");
        await consumer.ConsumeAsync(handler.HandleAsync);
        // --- end snippet ---

        email.Sent.Should().Equal("1:alice");
        slack.Messages.Should().Equal("Order 1 shipped: TRACK");
    }

    [Fact]
    public async Task MissingData_CreatesTheSummaryOnTheFirstEvent()
    {
        var projection = new CustomerSummaryProjection();
        projection.Current.Should().BeNull();

        await projection.HandleAsync(Envelope(new OrderPlacedEvent("1", "alice", 10m, T0), 1));
        await projection.HandleAsync(Envelope(new OrderPlacedEvent("2", "alice", 5m, T0), 2));

        projection.Current.Should().Be(new CustomerSummary("alice", 2, 15m));
    }

    [Fact]
    public async Task AtLeastOnce_IgnoresADuplicate()
    {
        var projection = new OrderStatusProjection();
        var placed = Envelope(new OrderPlacedEvent("1", "alice", 10m, T0), 1);
        var shipped = Envelope(new OrderShippedEvent("1", "T", T0), 2);

        // --- snippet: "At-Least-Once Processing" ---
        // The projection might be handed the same event twice
        await projection.HandleAsync(placed);
        await projection.HandleAsync(shipped);
        await projection.HandleAsync(shipped);  // Duplicate: ignored
        // --- end snippet ---
        await projection.HandleAsync(placed);   // An older event: ignored too

        projection.Current.Should().Be(new OrderStatus("Shipped", 2));
    }

    [Fact]
    public async Task Rebuild_FullAndPartial()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1", new OrderPlacedEvent("1", "alice", 10m, T0));
        await AppendAsync(store, "order-2",
            new OrderPlacedEvent("2", "bob", 20m, T0),
            new OrderCancelledEvent("2", "bob"));

        // --- snippet: "Rebuilding from Scratch" usage ---
        var rebuildService = new ProjectionRebuildService(store);
        var projection = new OrderListProjection();
        var finalState = await rebuildService.RebuildAsync(projection, StreamId.Global);
        Console.WriteLine($"Rebuilt with {finalState.Count} orders");
        // --- end snippet ---

        finalState.Select(o => o.OrderId).Should().Equal("1");

        var repository = new RecordingRepository();
        await new OrderProjectionRebuilder(store, repository).RebuildOrderProjection("1");
        repository.Saved.Should().ContainSingle().Which.OrderId.Should().Be("1");
    }

    [Fact]
    public async Task Chains_BothProjectionsSeeEveryEvent()
    {
        var store = NewEventStore();
        await AppendAsync(store, "order-1",
            new OrderPlacedEvent("1", "alice", 10m, T0),
            new OrderDeliveredEvent("1"));
        await AppendAsync(store, "order-2", new OrderReturnRequestedEvent("2"));

        // --- snippet: "Projection Chains" usage ---
        var statusProj = new OrderStatusProjection();
        var satisfactionProj = new CustomerSatisfactionProjection();

        await foreach (var envelope in store.ReadAsync(StreamId.Global, StreamPosition.Start))
        {
            await statusProj.HandleAsync(envelope);
            await satisfactionProj.HandleAsync(envelope);
        }
        // --- end snippet ---

        satisfactionProj.Current.Should().Be(new CustomerSatisfaction(1, 1));
        statusProj.Current.Status.Should().Be("Delivered");
    }

    [Fact]
    public async Task Testing_SnippetsFromThePage()
    {
        // --- snippet: "Test Event Application" ---
        // Arrange
        var projection = new OrderListProjection();
        var @event = new OrderPlacedEvent("ORD-001", "cust-123", 1500m, DateTimeOffset.UtcNow);
        var envelope = new EventEnvelope(
            new StreamId("order-ORD-001"),
            new StreamPosition(1),
            @event,
            EventMetadata.New(nameof(OrderPlacedEvent)));

        // Act
        await projection.HandleAsync(envelope);

        // Assert
        Assert.Contains(projection.Current, o => o.OrderId == "ORD-001" && o.Status == "Placed");
        // --- end snippet ---

        // --- snippet: "Test Multi-Stream Projection" ---
        // Arrange
        var summary = new CustomerOrderSummaryProjection("cust-123");

        var events = new object[]
        {
            new OrderPlacedEvent("ORD-001", "cust-123", 1000m, DateTimeOffset.UtcNow),
            new OrderPlacedEvent("ORD-002", "cust-123", 500m, DateTimeOffset.UtcNow),
            new OrderPlacedEvent("ORD-003", "cust-456", 200m, DateTimeOffset.UtcNow),
        };

        // Act
        var position = StreamPosition.Start;
        foreach (var e in events)
        {
            position = position.Next();
            await summary.HandleAsync(new EventEnvelope(
                StreamId.Global, position, e, EventMetadata.New(e.GetType().Name)));
        }

        // Assert: Only cust-123 orders counted
        Assert.Equal(2, summary.Current.OrderCount);
        Assert.Equal(1500m, summary.Current.TotalRevenue);
        // --- end snippet ---
    }

    [Fact]
    public async Task OrderListAndStatistics()
    {
        var list = new OrderListProjection();
        var stats = new CustomerOrderStatisticsProjection();
        var events = new object[]
        {
            new OrderPlacedEvent("1", "alice", 10m, T0),
            new OrderPlacedEvent("2", "alice", 5m, T0.AddDays(1)),
            new OrderShippedEvent("1", "T", T0),
        };
        for (var i = 0; i < events.Length; i++)
        {
            await list.HandleAsync(Envelope(events[i], i + 1));
            await stats.HandleAsync(Envelope(events[i], i + 1));
        }

        var search = new OrderListSearchService(list);
        search.SearchByCustomer("alice").Select(o => o.OrderId).Should().Equal("2", "1");
        search.SearchByStatus("Shipped").Select(o => o.OrderId).Should().Equal("1");
        search.GetCustomerTotalRevenue("alice").Should().Be(15m);
        stats.Current["alice"].Should().Be(new CustomerOrderStatistics("alice", 2, 15m));
    }

    [Fact]
    public async Task ResilientProcessor_DeadLettersAfterTheRetries()
    {
        var deadLetters = new InMemoryDeadLetterStore();
        var failing = new FailingProjection();
        var processor = new ResilientProjectionProcessor<int>(failing, deadLetters, NullLogger.Instance);

        await processor.ProcessAsync(Envelope(new OrderDeliveredEvent("1"), 1));

        failing.Attempts.Should().Be(4);
        var entries = new List<DeadLetterEntry>();
        await foreach (var entry in deadLetters.ReadAllAsync())
            entries.Add(entry);
        entries.Should().ContainSingle().Which.ConsumerId.Should().Be("order-projection");
    }

    private sealed class FailingProjection : Projection<int>
    {
        public int Attempts { get; private set; }

        protected override int Apply(int current, EventEnvelope @event)
        {
            Attempts++;
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class RecordingRepository : IOrderDetailsRepository
    {
        public List<OrderDetails> Saved { get; } = [];

        public Task SaveAsync(OrderDetails details, CancellationToken ct = default)
        {
            Saved.Add(details);
            return Task.CompletedTask;
        }

        public Task<OrderDetails?> GetByIdAsync(string orderId, CancellationToken ct = default)
            => Task.FromResult(Saved.LastOrDefault(d => d.OrderId == orderId));
    }

    private sealed class RecordingEmail : IEmailService
    {
        public List<string> Sent { get; } = [];

        public Task SendOrderPlacedAsync(string orderId, string customerId, CancellationToken ct)
        {
            Sent.Add($"{orderId}:{customerId}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSlack : ISlackService
    {
        public List<string> Messages { get; } = [];

        public Task NotifyAsync(string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class GuideEventTypeRegistry : IEventTypeRegistry
    {
        private static readonly ImmutableDictionary<string, Type> Types = new[]
        {
            typeof(OrderPlacedEvent), typeof(OrderConfirmedEvent), typeof(OrderShippedEvent),
            typeof(OrderCancelledEvent), typeof(OrderRefundedEvent), typeof(OrderDeliveredEvent),
            typeof(OrderReturnRequestedEvent),
        }.ToImmutableDictionary(t => t.Name);

        public bool TryGetType(string eventType, out Type? type) => Types.TryGetValue(eventType, out type);

        public string GetTypeName(Type type) => type.Name;
    }
}
