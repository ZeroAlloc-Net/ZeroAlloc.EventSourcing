using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing;

// The C# in docs/advanced/custom-projections.md, copied as it appears there between
// "--- snippet ---" markers and compiled against the public API. CustomProjectionsDocTests runs
// it. When a snippet changes in the docs, change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.CustomProjectionsDocs;

// --- snippet: "The Events in This Guide" ---
public record OrderLine(string ProductId, int Quantity, decimal Price);
public record OrderPlacedEvent(string OrderId, string CustomerId, decimal Total, IReadOnlyList<OrderLine> LineItems);
public record OrderShippedEvent(string OrderId, string CustomerEmail, string TrackingNumber);
public record OrderCancelledEvent(string OrderId);
public record StockReceivedEvent(string ProductId, int Quantity);
public record StockReservedEvent(string ProductId, int Quantity);
public record StockReleasedEvent(string ProductId, int Quantity);
public record DamagedStockEvent(string ProductId, int Quantity);

public sealed record OrderReadModel(string OrderId, decimal Total);
// --- end snippet ---

// --- snippet: "Pattern 1: Filtered Projections" ---
public class HighValueOrdersProjection : FilteredProjection<ImmutableDictionary<string, OrderReadModel>>
{
    private const decimal HighValueThreshold = 10_000m;

    public HighValueOrdersProjection()
    {
        Current = ImmutableDictionary<string, OrderReadModel>.Empty;
    }

    // Events this returns false for never reach Apply
    protected override bool IncludeEvent(EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => e.Total > HighValueThreshold,  // Low-value orders are ignored
        OrderCancelledEvent => true,
        _ => false
    };

    protected override ImmutableDictionary<string, OrderReadModel> Apply(
        ImmutableDictionary<string, OrderReadModel> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, new OrderReadModel(e.OrderId, e.Total)),
        OrderCancelledEvent e => current.Remove(e.OrderId),
        _ => current
    };

    public OrderReadModel? GetHighValueOrder(string orderId)
        => Current.GetValueOrDefault(orderId);
}
// --- end snippet ---

// --- snippet: "Pattern 2: Composite Projections" ---
/// <summary>
/// One projection can handle multiple read models.
/// Updates all models from single event.
/// </summary>
public sealed record OrderStatistics(
    ImmutableDictionary<string, int> OrderCountByCustomer,   // Read model 1
    ImmutableDictionary<string, decimal> RevenueByCustomer,  // Read model 2
    ImmutableList<(string OrderId, DateTimeOffset PlacedAt)> RecentOrders)  // Read model 3
{
    public static OrderStatistics Empty { get; } = new(
        ImmutableDictionary<string, int>.Empty,
        ImmutableDictionary<string, decimal>.Empty,
        ImmutableList<(string, DateTimeOffset)>.Empty);
}

public class OrderCompositeProjection : Projection<OrderStatistics>
{
    private const int RecentOrderLimit = 100;

    public OrderCompositeProjection()
    {
        Current = OrderStatistics.Empty;
    }

    protected override OrderStatistics Apply(OrderStatistics current, EventEnvelope @event)
    {
        if (@event.Event is not OrderPlacedEvent e)
            return current;

        // Keep only the last 100 recent orders; the time comes from the event's metadata
        var recent = current.RecentOrders.Add((e.OrderId, @event.Metadata.OccurredAt));
        if (recent.Count > RecentOrderLimit)
            recent = recent.RemoveAt(0);

        // Update all three models
        return new OrderStatistics(
            current.OrderCountByCustomer.SetItem(
                e.CustomerId, current.OrderCountByCustomer.GetValueOrDefault(e.CustomerId) + 1),
            current.RevenueByCustomer.SetItem(
                e.CustomerId, current.RevenueByCustomer.GetValueOrDefault(e.CustomerId) + e.Total),
            recent);
    }

    public int GetOrderCount(string customerId)
        => Current.OrderCountByCustomer.GetValueOrDefault(customerId);

    public decimal GetTotalRevenue(string customerId)
        => Current.RevenueByCustomer.GetValueOrDefault(customerId);

    public IReadOnlyList<(string OrderId, DateTimeOffset PlacedAt)> GetRecentOrders()
        => Current.RecentOrders;
}
// --- end snippet ---

// --- snippet: "Pattern 3: Stateful Projections" ---
public sealed record InventoryState(int OnHand, int Reserved, int Damaged)
{
    public static InventoryState Empty { get; } = new(0, 0, 0);

    public int Available => OnHand - Reserved;
}

public class InventoryProjection : Projection<ImmutableDictionary<string, InventoryState>>
{
    public InventoryProjection()
    {
        Current = ImmutableDictionary<string, InventoryState>.Empty;
    }

    protected override ImmutableDictionary<string, InventoryState> Apply(
        ImmutableDictionary<string, InventoryState> current,
        EventEnvelope @event) => @event.Event switch
    {
        StockReceivedEvent e => Update(current, e.ProductId, s => s with { OnHand = s.OnHand + e.Quantity }),
        StockReservedEvent e => Update(current, e.ProductId, s => s with { Reserved = s.Reserved + e.Quantity }),
        StockReleasedEvent e => Update(current, e.ProductId, s => s with { Reserved = s.Reserved - e.Quantity }),
        // Damaged stock leaves the stock on hand
        DamagedStockEvent e => Update(current, e.ProductId, s => s with
        {
            Damaged = s.Damaged + e.Quantity,
            OnHand = s.OnHand - e.Quantity
        }),
        _ => current
    };

    private static ImmutableDictionary<string, InventoryState> Update(
        ImmutableDictionary<string, InventoryState> current,
        string productId,
        Func<InventoryState, InventoryState> change)
        => current.SetItem(productId, change(current.GetValueOrDefault(productId) ?? InventoryState.Empty));

    public InventoryState? GetInventory(string productId)
        => Current.GetValueOrDefault(productId);

    public bool CanReserve(string productId, int quantity)
        => GetInventory(productId) is { } state && state.Available >= quantity;
}
// --- end snippet ---

// --- snippet: "Pattern 4: Denormalized Projections" ---
/// <summary>Flat view: one row per line item.</summary>
public sealed record LineItemView(string OrderId, string ProductId, int Quantity, decimal Price, string OrderStatus)
{
    public decimal Total => Quantity * Price;
}

/// <summary>
/// Instead of storing normalized Order + LineItems,
/// denormalize into flat table for fast queries.
/// </summary>
public class OrderLineItemProjection : Projection<ImmutableList<LineItemView>>
{
    public OrderLineItemProjection()
    {
        Current = ImmutableList<LineItemView>.Empty;
    }

    protected override ImmutableList<LineItemView> Apply(
        ImmutableList<LineItemView> current,
        EventEnvelope @event) => @event.Event switch
    {
        // Store each line item separately
        OrderPlacedEvent e => current.AddRange(e.LineItems.Select(li =>
            new LineItemView(e.OrderId, li.ProductId, li.Quantity, li.Price, "Placed"))),

        // Update status for all line items in this order
        OrderShippedEvent e => current.ConvertAll(li =>
            li.OrderId == e.OrderId ? li with { OrderStatus = "Shipped" } : li),

        _ => current
    };

    /// <summary>Query: "All line items for a product, across all orders"</summary>
    public List<LineItemView> GetLineItemsByProduct(string productId)
        => Current.Where(li => li.ProductId == productId).ToList();

    /// <summary>Query: "Total revenue for a product"</summary>
    public decimal GetProductRevenue(string productId)
        => GetLineItemsByProduct(productId).Sum(li => li.Total);

    /// <summary>Query: "All line items in an order"</summary>
    public List<LineItemView> GetOrderLineItems(string orderId)
        => Current.Where(li => li.OrderId == orderId).ToList();
}
// --- end snippet ---

// --- snippet: "Pattern 5: Batched Projections" ---
/// <summary>
/// Updates the read model on every event, but writes it to the store once per batch.
/// Trade-off: durability for performance.
/// </summary>
public class OrderTotalsBatchedProjection : BatchedProjection<ImmutableDictionary<string, decimal>>
{
    private readonly IProjectionStore _store;

    public OrderTotalsBatchedProjection(IProjectionStore store)
        : base(batchSize: 100)
    {
        _store = store;
        Current = ImmutableDictionary<string, decimal>.Empty;
    }

    // BatchedProjection derives from FilteredProjection: only included events are applied and batched
    protected override bool IncludeEvent(EventEnvelope @event) => @event.Event is OrderPlacedEvent;

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event)
        => @event.Event is OrderPlacedEvent e ? current.SetItem(e.OrderId, e.Total) : current;

    // Called when 100 events have been applied, and by FlushAsync
    protected override async ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct = default)
    {
        await _store.SaveAsync("order-totals", JsonSerializer.Serialize(Current), ct);
    }
}
// --- end snippet ---

// --- snippet: "Pattern 6: Projection Composition" ---
/// <summary>
/// Combines the results of other projections; it does not handle events itself.
/// </summary>
public class DashboardProjection
{
    private readonly OrderCompositeProjection _orderProjection;
    private readonly InventoryProjection _inventoryProjection;

    public DashboardProjection(OrderCompositeProjection orderProjection, InventoryProjection inventoryProjection)
    {
        _orderProjection = orderProjection;
        _inventoryProjection = inventoryProjection;
    }

    public sealed record DashboardData(int TotalOrders, decimal TotalRevenue, int AvailableInventory);

    public DashboardData GetDashboard(string customerId, string productId)
    {
        return new DashboardData(
            TotalOrders: _orderProjection.GetOrderCount(customerId),
            TotalRevenue: _orderProjection.GetTotalRevenue(customerId),
            AvailableInventory: _inventoryProjection.GetInventory(productId)?.Available ?? 0);
    }
}
// --- end snippet ---

// --- snippet: "Pattern 7: Event-Driven Side Effects" ---
public interface IEmailService
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Sends an email when an order ships. A stream consumer handler, not a projection: a projection
/// is rebuilt by replaying every event, which would send every email again.
/// </summary>
public class OrderShippingNotificationHandler
{
    private readonly IEmailService _emailService;

    public OrderShippingNotificationHandler(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Event is OrderShippedEvent e)
        {
            // Trigger side effect: send email
            await _emailService.SendAsync(
                to: e.CustomerEmail,
                subject: $"Your order {e.OrderId} has shipped",
                body: $"Tracking number: {e.TrackingNumber}",
                ct);
        }
    }
}
// --- end snippet ---

// --- snippet: "Pattern 8: Time-Windowed Projections" ---
/// <summary>
/// Projection that groups orders by the week they were placed in.
/// </summary>
public class WeeklyOrdersProjection : Projection<ImmutableDictionary<DateOnly, ImmutableList<OrderReadModel>>>
{
    public WeeklyOrdersProjection()
    {
        Current = ImmutableDictionary<DateOnly, ImmutableList<OrderReadModel>>.Empty;
    }

    protected override ImmutableDictionary<DateOnly, ImmutableList<OrderReadModel>> Apply(
        ImmutableDictionary<DateOnly, ImmutableList<OrderReadModel>> current,
        EventEnvelope @event)
    {
        if (@event.Event is not OrderPlacedEvent e)
            return current;

        // Group by the week the event occurred in, not the week it is processed in, so a
        // replay produces the same weeks
        var week = GetWeekStart(@event.Metadata.OccurredAt);
        var orders = current.GetValueOrDefault(week) ?? ImmutableList<OrderReadModel>.Empty;
        return current.SetItem(week, orders.Add(new OrderReadModel(e.OrderId, e.Total)));
    }

    private static DateOnly GetWeekStart(DateTimeOffset date)
    {
        var day = DateOnly.FromDateTime(date.UtcDateTime);
        return day.AddDays(-(int)day.DayOfWeek);
    }

    public IReadOnlyList<OrderReadModel> GetWeekOrders(DateTimeOffset dayInWeek)
        => Current.GetValueOrDefault(GetWeekStart(dayInWeek)) ?? ImmutableList<OrderReadModel>.Empty;

    public decimal GetWeekRevenue(DateTimeOffset dayInWeek)
        => GetWeekOrders(dayInWeek).Sum(o => o.Total);
}
// --- end snippet ---

// --- snippet: "Pattern 9: Projection State Snapshots" ---
/// <summary>The read model, with the position of the last event applied to it.</summary>
public sealed record OrderTotals(ImmutableDictionary<string, decimal> Totals, long LastPosition)
{
    public static OrderTotals Empty { get; } = new(ImmutableDictionary<string, decimal>.Empty, 0);
}

public class PersistentOrderTotalsProjection : Projection<OrderTotals>
{
    private const string Key = "order-totals";
    private readonly IProjectionStore _store;
    private int _eventCount;

    public PersistentOrderTotalsProjection(IProjectionStore store)
    {
        _store = store;
        Current = OrderTotals.Empty;
    }

    protected override OrderTotals Apply(OrderTotals current, EventEnvelope @event)
    {
        var totals = @event.Event is OrderPlacedEvent e ? current.Totals.SetItem(e.OrderId, e.Total) : current.Totals;
        return new OrderTotals(totals, @event.Position.Value);
    }

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        await base.HandleAsync(@event, ct);

        // Persist every 1000 events
        if (++_eventCount % 1000 == 0)
            await SaveAsync(ct);
    }

    public ValueTask SaveAsync(CancellationToken ct = default)
        => _store.SaveAsync(Key, JsonSerializer.Serialize(Current), ct);

    /// <summary>Restores the saved state; returns the position to resume reading after.</summary>
    public async ValueTask<StreamPosition> LoadAsync(CancellationToken ct = default)
    {
        var json = await _store.LoadAsync(Key, ct);
        if (json is not null)
            Current = JsonSerializer.Deserialize<OrderTotals>(json)!;
        return new StreamPosition(Current.LastPosition);
    }
}
// --- end snippet ---

// --- snippet: "Pattern 10: Error Handling in Projections" ---
public class ResilientOrderTotalsProjection : Projection<ImmutableDictionary<string, decimal>>
{
    private readonly ILogger _logger;

    public ResilientOrderTotalsProjection(ILogger logger)
    {
        _logger = logger;
        Current = ImmutableDictionary<string, decimal>.Empty;
    }

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e when e.Total < 0 =>
            throw new InvalidDataException($"Order {e.OrderId} has a negative total"),
        OrderPlacedEvent e => current.SetItem(e.OrderId, e.Total),
        _ => current
    };

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        try
        {
            await base.HandleAsync(@event, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Log but continue: Current keeps the state from before this event
            _logger.LogWarning(ex, "Skipped the event at position {Position}", @event.Position.Value);
        }
        // Any other exception is not caught and stops the projection
    }

    private static bool IsRecoverable(Exception ex)
    {
        // Bad data in one event should not stop the whole projection
        return ex is InvalidDataException or FormatException;
    }
}
// --- end snippet ---

// --- snippet: "Pattern 11: Rebuilding a Projection in Place" ---
public class RebuildableOrderTotalsProjection : ReplayableProjection<ImmutableDictionary<string, decimal>>
{
    // Current starts at the empty dictionary, and each rebuild resets to it
    public RebuildableOrderTotalsProjection()
        : base(ImmutableDictionary<string, decimal>.Empty)
    {
    }

    public override string GetProjectionKey() => "rebuilt-order-totals";

    protected override ImmutableDictionary<string, decimal> Apply(
        ImmutableDictionary<string, decimal> current,
        EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current.SetItem(e.OrderId, e.Total),
        OrderCancelledEvent e => current.Remove(e.OrderId),
        _ => current
    };
}
// --- end snippet ---

public static class CustomProjectionsUsage
{
    public static async Task<RebuildableOrderTotalsProjection> ReplayableUsage(
        IEventStore eventStore, IProjectionStore projectionStore)
    {
        // --- snippet: "Pattern 11" usage ---
        var projection = new RebuildableOrderTotalsProjection();

        // Replays the stream from the start and saves the result as JSON under "rebuilt-order-totals"
        await projection.RebuildAsync(projectionStore, new StreamId("orders"), eventStore);
        // --- end snippet ---
        return projection;
    }

    public static async Task BatchedUsage(IEventStore eventStore, IProjectionStore projectionStore)
    {
        // --- snippet: "Pattern 5" usage ---
        var projection = new OrderTotalsBatchedProjection(projectionStore);
        await foreach (var envelope in eventStore.ReadAsync(StreamId.Global))
        {
            await projection.HandleAsync(envelope);
        }

        // Write the last, partial batch
        await projection.FlushAsync();
        // --- end snippet ---
    }

    public static async Task PersistentUsage(IEventStore eventStore, IProjectionStore projectionStore)
    {
        // --- snippet: "Pattern 9" usage ---
        var projection = new PersistentOrderTotalsProjection(projectionStore);

        // Resume after the last saved position instead of replaying everything
        var resumeAfter = await projection.LoadAsync();
        await foreach (var envelope in eventStore.ReadAsync(new StreamId("orders"), resumeAfter))
        {
            await projection.HandleAsync(envelope);
        }

        await projection.SaveAsync();
        // --- end snippet ---
    }

    public static async Task SideEffectWiring(
        IEventStore eventStore, ICheckpointStore checkpointStore, OrderShippingNotificationHandler handler, CancellationToken ct)
    {
        // --- snippet: "Pattern 7" usage ---
        // The consumer checkpoints what it handled, so a restart does not send an email twice
        var consumer = new StreamConsumer(eventStore, checkpointStore, consumerId: "shipping-emails");
        await consumer.ConsumeAsync(handler.HandleAsync, ct);
        // --- end snippet ---
    }
}

