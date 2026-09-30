using System.Collections.Immutable;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

// The C# in docs/core-concepts/projections.md, copied as it appears there between
// "--- snippet ---" markers and compiled against the public API. CoreProjectionsDocTests runs it.
// When a snippet changes in the docs, change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.CoreProjectionsDocs;

// --- snippet: "The Events in This Page" ---
public record OrderPlacedEvent(string OrderId, string CustomerId, string CustomerName, decimal Total);
public record OrderShippedEvent(string OrderId, string TrackingNumber);
public record OrderDeliveredEvent(string OrderId);
public record OrderCancelledEvent(string OrderId, string CustomerId, decimal Amount);
// --- end snippet ---

// --- snippet: "Single-Stream Projections" ---
public sealed record OrderDetails(
    string OrderId,
    decimal Total,
    string Status,
    string? TrackingNumber,
    DateTimeOffset? ShippedAt)
{
    public static OrderDetails Empty { get; } = new("", 0m, "New", null, null);
}

// Project a single Order's events into a read model
public class OrderDetailsProjection : Projection<OrderDetails>
{
    public OrderDetailsProjection()
    {
        // Current starts at default(T), which is null for a record: set a starting value
        Current = OrderDetails.Empty;
    }

    protected override OrderDetails Apply(OrderDetails current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => current with
            {
                OrderId = e.OrderId,
                Total = e.Total,
                Status = "Placed"
            },
            OrderShippedEvent e => current with
            {
                Status = "Shipped",
                TrackingNumber = e.TrackingNumber,
                // The time the event occurred, not the time it is processed: a replay gives the same value
                ShippedAt = @event.Metadata.OccurredAt
            },
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Multi-Stream Projections" ---
// Project all orders into a customer summary
public record CustomerOrderSummary(
    string CustomerId,
    int OrderCount,
    decimal TotalRevenue,
    ImmutableList<string> RecentOrderIds
);

public class CustomerSummaryProjection : Projection<CustomerOrderSummary>
{
    private readonly string _customerId;

    public CustomerSummaryProjection(string customerId)
    {
        _customerId = customerId;
        Current = new CustomerOrderSummary(customerId, 0, 0m, ImmutableList<string>.Empty);
    }

    protected override CustomerOrderSummary Apply(
        CustomerOrderSummary current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e when e.CustomerId == _customerId =>
                current with
                {
                    OrderCount = current.OrderCount + 1,
                    TotalRevenue = current.TotalRevenue + e.Total,
                    RecentOrderIds = current.RecentOrderIds.Insert(0, e.OrderId).Take(10).ToImmutableList()
                },
            OrderCancelledEvent e when e.CustomerId == _customerId =>
                current with
                {
                    TotalRevenue = current.TotalRevenue - e.Amount
                },
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Materialized Views" ---
public record OrdersDataStore  // One row of the database table
{
    public string OrderId { get; init; } = "";
    public decimal Total { get; init; }
    public string Status { get; init; } = "";
}

// Your data-access code for the table: not part of the library
public interface IOrdersTable
{
    ValueTask UpsertAsync(OrdersDataStore row, CancellationToken ct);
}

public class OrdersMaterializationProjection : Projection<ImmutableDictionary<string, OrdersDataStore>>
{
    private readonly IOrdersTable _table;

    public OrdersMaterializationProjection(IOrdersTable table)
    {
        _table = table;
        Current = ImmutableDictionary<string, OrdersDataStore>.Empty;
    }

    protected override ImmutableDictionary<string, OrdersDataStore> Apply(
        ImmutableDictionary<string, OrdersDataStore> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                ApplyOrderUpdate(current, e.OrderId, row => row with { Total = e.Total, Status = "Placed" }),
            OrderShippedEvent e =>
                ApplyOrderUpdate(current, e.OrderId, row => row with { Status = "Shipped" }),
            _ => current
        };
    }

    private static ImmutableDictionary<string, OrdersDataStore> ApplyOrderUpdate(
        ImmutableDictionary<string, OrdersDataStore> current,
        string orderId,
        Func<OrdersDataStore, OrdersDataStore> update)
    {
        var row = current.GetValueOrDefault(orderId) ?? new OrdersDataStore { OrderId = orderId };
        return current.SetItem(orderId, update(row));
    }

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        await base.HandleAsync(@event, ct);

        // Persist the row this event changed
        var orderId = @event.Event switch
        {
            OrderPlacedEvent e => e.OrderId,
            OrderShippedEvent e => e.OrderId,
            _ => null
        };
        if (orderId is not null)
            await _table.UpsertAsync(Current[orderId], ct);
    }
}
// --- end snippet ---

// --- snippet: "Counts and Aggregations" ---
public record RevenueByMonth(ImmutableDictionary<string, decimal> ByMonth)
{
    public static RevenueByMonth Empty { get; } = new(ImmutableDictionary<string, decimal>.Empty);
}

public class RevenueProjection : Projection<RevenueByMonth>
{
    public RevenueProjection()
    {
        Current = RevenueByMonth.Empty;
    }

    protected override RevenueByMonth Apply(RevenueByMonth current, EventEnvelope @event)
    {
        // The month the event occurred in, from its metadata
        var month = @event.Metadata.OccurredAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        return @event.Event switch
        {
            OrderPlacedEvent e => current with { ByMonth = Add(current.ByMonth, month, e.Total) },
            OrderCancelledEvent e => current with { ByMonth = Add(current.ByMonth, month, -e.Amount) },
            _ => current
        };
    }

    private static ImmutableDictionary<string, decimal> Add(
        ImmutableDictionary<string, decimal> byMonth, string month, decimal amount)
        => byMonth.SetItem(month, byMonth.GetValueOrDefault(month) + amount);
}
// --- end snippet ---

// --- snippet: "Search Indices" ---
public record SearchDocument(string OrderId, string CustomerName, decimal Total, IReadOnlyList<string> Keywords);

public record SearchIndex(ImmutableList<SearchDocument> Documents)
{
    public static SearchIndex Empty { get; } = new(ImmutableList<SearchDocument>.Empty);

    public IEnumerable<SearchDocument> Search(string term)
        => Documents.Where(d => d.Keywords.Any(k => k.Contains(term, StringComparison.OrdinalIgnoreCase)));
}

public class OrderSearchIndexProjection : Projection<SearchIndex>
{
    public OrderSearchIndexProjection()
    {
        Current = SearchIndex.Empty;
    }

    protected override SearchIndex Apply(SearchIndex current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current with
                {
                    Documents = current.Documents.Add(
                        new SearchDocument(e.OrderId, e.CustomerName, e.Total, [e.OrderId, e.CustomerName]))
                },
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Implementing a projection" ---
public sealed record OrderData(string OrderId = "", string Status = "New", decimal Total = 0m, string? TrackingNumber = null);

public class OrderProjection : Projection<OrderData>
{
    public OrderProjection()
    {
        Current = new OrderData();  // Initialize
    }

    protected override OrderData Apply(OrderData current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => current with { OrderId = e.OrderId, Status = "Placed", Total = e.Total },
            OrderShippedEvent e => current with { Status = "Shipped", TrackingNumber = e.TrackingNumber },
            _ => current
        };
    }
}
// --- end snippet ---

public static class Rebuilding
{
    // --- snippet: "Projection Rebuilding and Replay" ---
    // Rebuild a projection by replaying all events
    public static async Task<T> RebuildProjectionAsync<T>(
        IEventStore eventStore,
        StreamId streamId,
        Projection<T> projection)
    {
        // Read from the beginning
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            // Reapply each event
            await projection.HandleAsync(envelope);
        }

        // Projection.Current is now fully rebuilt
        return projection.Current;
    }
    // --- end snippet ---
}

// --- snippet: "Rebuild without downtime" ---
public sealed class OrderQueryService
{
    private OrderProjection _active = new();

    // Queries always read the active projection
    public OrderData Current => Volatile.Read(ref _active).Current;

    public async Task RebuildAsync(IEventStore eventStore, StreamId streamId)
    {
        // 1. Create a new projection instance
        var rebuilt = new OrderProjection();

        // 2. Rebuild it while queries keep using the old one
        await Rebuilding.RebuildProjectionAsync(eventStore, streamId, rebuilt);

        // 3. Once the rebuild completes, switch
        Volatile.Write(ref _active, rebuilt);
    }
}
// --- end snippet ---

// --- snippet: "Idempotent projection" ---
// Idempotent projection
public class IdempotentOrderProjection : Projection<OrderData>
{
    public IdempotentOrderProjection() => Current = new OrderData();

    protected override OrderData Apply(OrderData current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                // Safe to apply twice: it sets values instead of adding to them
                current with { OrderId = e.OrderId, Total = e.Total },
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Event deduplication (optional)" ---
// If exactly-once is critical, track processed event positions
public class TrackingProjection : Projection<(OrderData Data, StreamPosition LastPosition)>
{
    public TrackingProjection() => Current = (new OrderData(), StreamPosition.Start);

    protected override (OrderData Data, StreamPosition LastPosition) Apply(
        (OrderData Data, StreamPosition LastPosition) current,
        EventEnvelope @event)
    {
        // Skip if we've already processed this position
        // (StreamPosition has no ordering operators; compare the values)
        if (current.LastPosition.Value >= @event.Position.Value)
            return current;

        var newData = ApplyEvent(current.Data, @event.Event);
        return (newData, @event.Position);
    }

    private static OrderData ApplyEvent(OrderData data, object @event) => @event switch
    {
        OrderPlacedEvent e => data with { OrderId = e.OrderId, Total = data.Total + e.Total },
        _ => data
    };
}
// --- end snippet ---

// --- snippet: "Real-Time Analytics Dashboard" projection ---
public sealed record OrderMetrics(int Placed, int Shipped, decimal Revenue);

public class OrderMetricsProjection : Projection<OrderMetrics>
{
    public OrderMetricsProjection() => Current = new OrderMetrics(0, 0, 0m);

    protected override OrderMetrics Apply(OrderMetrics current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current with { Placed = current.Placed + 1, Revenue = current.Revenue + e.Total },
        OrderShippedEvent => current with { Shipped = current.Shipped + 1 },
        _ => current
    };
}
// --- end snippet ---

public static class Dashboard
{
    public static async Task<OrderMetricsProjection> SubscribeAsync(IEventStore eventStore, StreamId streamId, Func<Task> appendEvents)
    {
        // --- snippet: "Real-Time Analytics Dashboard" ---
        // Project real-time order metrics
        var dashboardProjection = new OrderMetricsProjection();

        // Subscribe to new events on a stream
        await using var subscription = await eventStore.SubscribeAsync(
            id: streamId,
            from: StreamPosition.Start,
            handler: async (envelope, ct) =>
            {
                await dashboardProjection.HandleAsync(envelope, ct);
                // Dashboard updates in real-time
            }
        );
        await subscription.StartAsync();  // events are delivered once the subscription is started
        // --- end snippet ---

        await appendEvents();
        return dashboardProjection;
    }
}

// --- snippet: "Event-Driven Notifications" ---
public sealed record Notification(string Message);

public interface INotificationService
{
    Task SendAsync(Notification notification, CancellationToken ct);
}

// A stream consumer handler, not a projection: rebuilding a projection replays every event
// and would send every notification again
public class OrderNotificationHandler
{
    private readonly INotificationService _notificationService;

    public OrderNotificationHandler(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public async Task HandleAsync(EventEnvelope @event, CancellationToken ct)
    {
        var notification = @event.Event switch
        {
            OrderShippedEvent e => new Notification($"Order {e.OrderId} shipped with {e.TrackingNumber}"),
            OrderDeliveredEvent e => new Notification($"Order {e.OrderId} delivered"),
            _ => null
        };

        if (notification is not null)
            await _notificationService.SendAsync(notification, ct);
    }
}
// --- end snippet ---

// --- snippet: "Cross-Aggregate Relationships" ---
// Project customer-order relationships
public class CustomerOrderGraphProjection : Projection<ImmutableDictionary<string, ImmutableList<string>>>
{
    public CustomerOrderGraphProjection() => Current = ImmutableDictionary<string, ImmutableList<string>>.Empty;

    protected override ImmutableDictionary<string, ImmutableList<string>> Apply(
        ImmutableDictionary<string, ImmutableList<string>> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.SetItem(e.CustomerId, (current.GetValueOrDefault(e.CustomerId) ?? []).Add(e.OrderId)),
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Integration with Repositories and Queries" projection ---
public class OrderListProjection : Projection<ImmutableDictionary<string, OrderData>>
{
    public OrderListProjection() => Current = ImmutableDictionary<string, OrderData>.Empty;

    protected override ImmutableDictionary<string, OrderData> Apply(
        ImmutableDictionary<string, OrderData> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, new OrderData(e.OrderId, "Placed", e.Total)),
        OrderShippedEvent e when current.TryGetValue(e.OrderId, out var order) =>
            current.SetItem(e.OrderId, order with { Status = "Shipped", TrackingNumber = e.TrackingNumber }),
        _ => current
    };
}
// --- end snippet ---

public static class Queries
{
    public static async Task<(int Shipped, decimal Revenue)> UseBothAsync(
        IAggregateRepository<Order, OrderId> repository,
        OrderId orderId,
        OrderListProjection orderProjection,
        CustomerSummaryProjection customerProjection)
    {
        // --- snippet: "Integration with Repositories and Queries" ---
        // Aggregate: Single order details and behavior
        using var order = (await repository.LoadAsync(orderId)).Value;
        order.Ship("TRACK-123");
        await repository.SaveAsync(order, orderId);

        // Projection: List all orders, search, filter
        var allOrders = orderProjection.Current.Values;
        var shippedOrders = allOrders.Where(o => o.Status == "Shipped");

        // Projection: Customer summary across many orders
        var customerSummary = customerProjection.Current;
        var revenue = customerSummary.TotalRevenue;
        // --- end snippet ---

        return (shippedOrders.Count(), revenue);
    }
}

public static class Usage
{
    public static async Task<OrderDetails> SingleStreamAsync(IEventStore eventStore, string orderId)
    {
        // --- snippet: "Single-Stream Projections" usage ---
        // Usage: Load a single order's projection
        var projection = new OrderDetailsProjection();
        await foreach (var envelope in eventStore.ReadAsync(new StreamId($"order-{orderId}"), StreamPosition.Start))
        {
            await projection.HandleAsync(envelope);
        }

        var orderDetails = projection.Current;
        // --- end snippet ---
        return orderDetails;
    }

    public static async Task<Dictionary<string, CustomerSummaryProjection>> MultiStreamAsync(IEventStore eventStore)
    {
        // --- snippet: "Multi-Stream Projections" usage ---
        // Usage: Scan all order events and aggregate by customer
        var customerSummaries = new Dictionary<string, CustomerSummaryProjection>();

        // StreamId.Global reads every event in the store, in append order
        await foreach (var envelope in eventStore.ReadAsync(StreamId.Global, StreamPosition.Start))
        {
            var customerId = envelope.Event switch
            {
                OrderPlacedEvent e => e.CustomerId,
                OrderCancelledEvent e => e.CustomerId,
                _ => null
            };
            if (customerId is null)
                continue;

            if (!customerSummaries.TryGetValue(customerId, out var projection))
                customerSummaries[customerId] = projection = new CustomerSummaryProjection(customerId);

            await projection.HandleAsync(envelope);
        }
        // --- end snippet ---
        return customerSummaries;
    }

    // --- snippet: "Mitigating eventual consistency" ---
    // 1. Wait until the consumer that feeds the projection has passed your write.
    // For a consumer of one stream, targetPosition is the NextExpectedVersion your append returned.
    public static async ValueTask WaitForProjectionCatchUp(
        ICheckpointStore checkpointStore,
        string consumerId,
        StreamPosition targetPosition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var checkpoint = await checkpointStore.ReadAsync(consumerId);
            if (checkpoint is { } position && position.Value >= targetPosition.Value)
                return;  // Caught up

            await Task.Delay(100);
        }

        throw new TimeoutException($"Projection did not catch up within {timeout}");
    }

    // 2. Return the position with the result, so a caller can tell how current it is
    public record OrderProjectionResult(OrderDetails Details, StreamPosition Position);

    // 3. Accept eventual consistency
    // For most UIs, small delays are acceptable
    // --- end snippet ---
}
