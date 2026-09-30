using System.Collections.Immutable;
using System.Text.Json;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

// The projection strategies in docs/performance/optimization.md, sections 3 and 5A, copied as they
// appear there between "--- snippet ---" markers and compiled against the public API.
// OptimizationDocTests runs them. When a snippet changes in the docs, change it here as well.
// See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.OptimizationDocs;

// --- snippet: "3. Projection Optimization" events ---
public record OrderPlacedEvent(string OrderId, string CustomerId, decimal Total);
public record OrderShippedEvent(string OrderId, DateTimeOffset ShippedAt);
public record CustomerRenamedEvent(string CustomerId, string Name);

public sealed record OrderReadModel(string OrderId, decimal Total, bool IsShipped);
// --- end snippet ---

// --- snippet: "Strategy 3A: Filter Events in Projections" ---
public class OrderProjection : FilteredProjection<ImmutableDictionary<string, OrderReadModel>>
{
    public OrderProjection()
    {
        Current = ImmutableDictionary<string, OrderReadModel>.Empty;
    }

    // Only order events reach Apply; everything else is skipped before any work is done
    protected override bool IncludeEvent(EventEnvelope @event)
        => @event.Event is OrderPlacedEvent or OrderShippedEvent;

    protected override ImmutableDictionary<string, OrderReadModel> Apply(
        ImmutableDictionary<string, OrderReadModel> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, new OrderReadModel(e.OrderId, e.Total, IsShipped: false)),
        OrderShippedEvent e when current.TryGetValue(e.OrderId, out var model) =>
            current.SetItem(e.OrderId, model with { IsShipped = true }),
        _ => current
    };
}
// --- end snippet ---

// --- snippet: "Strategy 3B: Batch Projection Updates" inefficient ---
// INEFFICIENT: Write the read model after each event
public class EveryEventOrderTotalsProjection : Projection<ImmutableDictionary<string, decimal>>
{
    private readonly IProjectionStore _projectionStore;

    public EveryEventOrderTotalsProjection(IProjectionStore projectionStore)
    {
        _projectionStore = projectionStore;
        Current = ImmutableDictionary<string, decimal>.Empty;
    }

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current.SetItem(e.OrderId, e.Total) : current;

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        await base.HandleAsync(@event, ct);
        await _projectionStore.SaveAsync("order-totals", JsonSerializer.Serialize(Current), ct);
    }
}
// --- end snippet ---

// --- snippet: "Strategy 3B: Batch Projection Updates" efficient ---
// EFFICIENT: Batch writes; BatchedProjection calls FlushBatchAsync once per 100 events
public class BatchedOrderTotalsProjection : BatchedProjection<ImmutableDictionary<string, decimal>>
{
    private readonly IProjectionStore _projectionStore;

    public BatchedOrderTotalsProjection(IProjectionStore projectionStore)
        : base(batchSize: 100)
    {
        _projectionStore = projectionStore;
        Current = ImmutableDictionary<string, decimal>.Empty;
    }

    protected override bool IncludeEvent(EventEnvelope @event) => @event.Event is OrderPlacedEvent;

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current.SetItem(e.OrderId, e.Total) : current;

    protected override async ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct = default)
        => await _projectionStore.SaveAsync("order-totals", JsonSerializer.Serialize(Current), ct);
}
// --- end snippet ---

// --- snippet: "Strategy 3C: Multiple Specialized Projections" ---
// MONOLITHIC: One read model for all queries
public sealed record OrderModel(
    string OrderId,
    decimal Total,
    bool IsShipped,
    int LineItemCount,
    DateTimeOffset CreatedAt,
    string CustomerName,
    IReadOnlyList<string> Tags);  // ... 20 more fields

// SPECIALIZED: Multiple projections, each optimized for one query
public class OrderTotalsProjection : Projection<decimal>  // For: "Sum of all orders"
{
    protected override decimal Apply(decimal current, EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current + e.Total : current;
}

public class ShippingProjection : Projection<ImmutableList<(string OrderId, DateTimeOffset ShippedAt)>>  // For: "Which orders shipped today?"
{
    public ShippingProjection() => Current = [];

    protected override ImmutableList<(string OrderId, DateTimeOffset ShippedAt)> Apply(
        ImmutableList<(string OrderId, DateTimeOffset ShippedAt)> current,
        EventEnvelope @event)
        => @event.Event is OrderShippedEvent e ? current.Add((e.OrderId, e.ShippedAt)) : current;
}

public class CustomersProjection : Projection<ImmutableDictionary<string, ImmutableList<string>>>  // For: "Orders by customer"
{
    public CustomersProjection() => Current = ImmutableDictionary<string, ImmutableList<string>>.Empty;

    protected override ImmutableDictionary<string, ImmutableList<string>> Apply(
        ImmutableDictionary<string, ImmutableList<string>> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e
            ? current.SetItem(e.CustomerId, (current.GetValueOrDefault(e.CustomerId) ?? []).Add(e.OrderId))
            : current;
}
// --- end snippet ---

// --- snippet: "Strategy 5A: Use In-Memory Projections for Hot Data" ---
/// <summary>
/// Keeps today's orders in memory. One thread feeds events; any number of threads query.
/// Current is an immutable dictionary that each event replaces as a whole, so a query never
/// sees a half-applied update and needs no lock.
/// </summary>
public class InMemoryOrderProjection : Projection<ImmutableDictionary<string, OrderReadModel>>
{
    public InMemoryOrderProjection()
    {
        Current = ImmutableDictionary<string, OrderReadModel>.Empty;
    }

    protected override ImmutableDictionary<string, OrderReadModel> Apply(
        ImmutableDictionary<string, OrderReadModel> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, new OrderReadModel(e.OrderId, e.Total, IsShipped: false)),
        OrderShippedEvent e when current.TryGetValue(e.OrderId, out var model) =>
            current.SetItem(e.OrderId, model with { IsShipped = true }),
        _ => current
    };

    public OrderReadModel? GetOrder(string orderId)
        => Current.GetValueOrDefault(orderId);
}
// --- end snippet ---

public sealed class OptimizationDocTests
{
    private static EventEnvelope Envelope(object @event, long position)
        => new(new StreamId("orders"), new StreamPosition(position), @event, EventMetadata.New(@event.GetType().Name));

    private static async Task FeedAsync<T>(Projection<T> projection, params object[] events)
    {
        for (var i = 0; i < events.Length; i++)
            await projection.HandleAsync(Envelope(events[i], i + 1));
    }

    private static readonly object[] Orders =
    [
        new OrderPlacedEvent("1", "alice", 10m),
        new CustomerRenamedEvent("alice", "Alice"),
        new OrderPlacedEvent("2", "alice", 5m),
        new OrderShippedEvent("1", DateTimeOffset.UnixEpoch),
    ];

    [Fact]
    public async Task Filtered_AppliesOnlyOrderEvents()
    {
        var projection = new OrderProjection();
        await FeedAsync(projection, Orders);

        projection.Current["1"].IsShipped.Should().BeTrue();
        projection.Current["2"].IsShipped.Should().BeFalse();
    }

    [Fact]
    public async Task Batched_WritesOncePerBatch()
    {
        var everyEvent = new CountingStore();
        var batched = new CountingStore();
        var events = Enumerable.Range(1, 250).Select(i => (object)new OrderPlacedEvent($"{i}", "c", i)).ToArray();

        await FeedAsync(new EveryEventOrderTotalsProjection(everyEvent), events);
        var projection = new BatchedOrderTotalsProjection(batched);
        await FeedAsync(projection, events);
        await projection.FlushAsync();

        everyEvent.Saves.Should().Be(250);
        batched.Saves.Should().Be(3);
    }

    [Fact]
    public async Task Specialized_EachAnswersItsQuery()
    {
        var totals = new OrderTotalsProjection();
        var shipping = new ShippingProjection();
        var customers = new CustomersProjection();
        await FeedAsync(totals, Orders);
        await FeedAsync(shipping, Orders);
        await FeedAsync(customers, Orders);

        totals.Current.Should().Be(15m);
        shipping.Current.Should().ContainSingle().Which.OrderId.Should().Be("1");
        customers.Current["alice"].Should().Equal("1", "2");
    }

    [Fact]
    public async Task InMemory_AnswersQueries()
    {
        var projection = new InMemoryOrderProjection();
        await FeedAsync(projection, Orders);

        projection.GetOrder("1")!.IsShipped.Should().BeTrue();
        projection.GetOrder("3").Should().BeNull();
    }

    private sealed class CountingStore : IProjectionStore
    {
        private readonly InMemoryProjectionStore _inner = new();

        public int Saves { get; private set; }

        public ValueTask SaveAsync(string key, string state, CancellationToken ct = default)
        {
            Saves++;
            return _inner.SaveAsync(key, state, ct);
        }

        public ValueTask<string?> LoadAsync(string key, CancellationToken ct = default) => _inner.LoadAsync(key, ct);

        public ValueTask ClearAsync(string key, CancellationToken ct = default) => _inner.ClearAsync(key, ct);
    }
}
