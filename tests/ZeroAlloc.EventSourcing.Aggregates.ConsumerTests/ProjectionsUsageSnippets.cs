using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing;

// The C# in docs/usage-guides/projections-usage.md, copied as it appears there and compiled
// against the public API. Each block between "--- snippet ---" markers is one code block of the
// page. When a snippet changes in the docs, change it here as well. ProjectionsUsageTests runs
// them. See issue #402.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.ProjectionsUsage;

// --- snippet: "The Events in This Guide" ---
public record OrderPlacedEvent(string OrderId, string CustomerId, decimal Total, DateTimeOffset PlacedAt);
public record OrderConfirmedEvent(string OrderId, DateTimeOffset ConfirmedAt);
public record OrderShippedEvent(string OrderId, string TrackingNumber, DateTimeOffset ShippedAt);
public record OrderCancelledEvent(string OrderId, string CustomerId);
public record OrderRefundedEvent(string OrderId, string CustomerId, decimal RefundAmount);
public record OrderDeliveredEvent(string OrderId);
public record OrderReturnRequestedEvent(string OrderId);
// --- end snippet ---

// --- snippet: "Single-Stream Projections" ---
public sealed record OrderDetails(
    string OrderId,
    string CustomerId,
    decimal Total,
    string Status,
    string? TrackingNumber,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt)
{
    public static OrderDetails Empty { get; } = new("", "", 0m, "New", null, null, null, null);
}

// Project a single Order's events
public class OrderDetailsProjection : Projection<OrderDetails>
{
    public OrderDetailsProjection()
    {
        // Current starts at default(T), which is null for a class or record: set a starting value
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
                CustomerId = e.CustomerId,
                Status = "Placed",
                PlacedAt = e.PlacedAt
            },
            OrderConfirmedEvent e => current with
            {
                Status = "Confirmed",
                ConfirmedAt = e.ConfirmedAt
            },
            OrderShippedEvent e => current with
            {
                Status = "Shipped",
                TrackingNumber = e.TrackingNumber,
                ShippedAt = e.ShippedAt
            },
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Multi-Stream Projections" ---
public sealed record CustomerOrderSummary(
    string CustomerId,
    int OrderCount,
    decimal TotalRevenue,
    ImmutableList<string> RecentOrderIds);

// Project all orders into a customer summary
public class CustomerOrderSummaryProjection : Projection<CustomerOrderSummary>
{
    private readonly string _customerId;

    public CustomerOrderSummaryProjection(string customerId)
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
            // Only process events from this customer's orders
            OrderPlacedEvent e when e.CustomerId == _customerId => current with
            {
                OrderCount = current.OrderCount + 1,
                TotalRevenue = current.TotalRevenue + e.Total,
                RecentOrderIds = current.RecentOrderIds.Insert(0, e.OrderId).Take(10).ToImmutableList()
            },
            OrderRefundedEvent e when e.CustomerId == _customerId => current with
            {
                TotalRevenue = current.TotalRevenue - e.RefundAmount
            },
            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Pattern 1: Materialization (Denormalized View)" ---
// Your read-model storage, for example a table with one row per order
public interface IOrderDetailsRepository
{
    Task SaveAsync(OrderDetails details, CancellationToken ct = default);
    Task<OrderDetails?> GetByIdAsync(string orderId, CancellationToken ct = default);
}

public sealed class MaterializedOrderDetailsProjection : OrderDetailsProjection
{
    private readonly IOrderDetailsRepository _repository;

    public MaterializedOrderDetailsProjection(IOrderDetailsRepository repository)
    {
        _repository = repository;
    }

    // Apply stays a pure function; persisting is a side effect, so it goes in HandleAsync
    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        var before = Current;
        await base.HandleAsync(@event, ct);

        // Persist to read model
        if (!ReferenceEquals(before, Current))
            await _repository.SaveAsync(Current, ct);
    }
}
// --- end snippet ---

// --- snippet: "Pattern 2: Counts and Aggregations" ---
public class CustomerOrderCountProjection : Projection<ImmutableDictionary<string, int>>
{
    public CustomerOrderCountProjection()
    {
        Current = ImmutableDictionary<string, int>.Empty;
    }

    protected override ImmutableDictionary<string, int> Apply(
        ImmutableDictionary<string, int> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.SetItem(e.CustomerId, current.GetValueOrDefault(e.CustomerId) + 1),

            OrderCancelledEvent e when current.TryGetValue(e.CustomerId, out var count) =>
                current.SetItem(e.CustomerId, count - 1),

            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Pattern 3: Search Indices" ---
public record OrderIndex(string OrderId, string CustomerId, decimal Total, string Status);

public class OrderSearchProjection : Projection<ImmutableList<OrderIndex>>
{
    public OrderSearchProjection()
    {
        Current = ImmutableList<OrderIndex>.Empty;
    }

    protected override ImmutableList<OrderIndex> Apply(ImmutableList<OrderIndex> current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.Add(new OrderIndex(e.OrderId, e.CustomerId, e.Total, "Placed")),

            OrderShippedEvent e =>
                current.Select(o =>
                    o.OrderId == e.OrderId
                        ? o with { Status = "Shipped" }
                        : o
                ).ToImmutableList(),

            _ => current
        };
    }
}

// Usage: Search index allows quick lookups
public class OrderSearchService
{
    private readonly OrderSearchProjection _projection;

    public OrderSearchService(OrderSearchProjection projection)
    {
        _projection = projection;
    }

    public List<OrderIndex> SearchByCustomer(string customerId)
    {
        return _projection.Current
            .Where(o => o.CustomerId == customerId)
            .ToList();
    }

    public List<OrderIndex> SearchByStatus(string status)
    {
        return _projection.Current
            .Where(o => o.Status == status)
            .ToList();
    }
}
// --- end snippet ---

// --- snippet: "Pattern 4: Notifications and Side Effects" ---
public interface IEmailService
{
    Task SendOrderPlacedAsync(string orderId, string customerId, CancellationToken ct);
}

public interface ISlackService
{
    Task NotifyAsync(string message, CancellationToken ct);
}

public class OrderNotificationHandler
{
    private readonly IEmailService _emailService;
    private readonly ISlackService _slackService;

    public OrderNotificationHandler(IEmailService email, ISlackService slack)
    {
        _emailService = email;
        _slackService = slack;
    }

    public async Task HandleAsync(EventEnvelope @event, CancellationToken ct)
    {
        switch (@event.Event)
        {
            case OrderPlacedEvent e:
                // Send confirmation email
                await _emailService.SendOrderPlacedAsync(e.OrderId, e.CustomerId, ct);
                break;

            case OrderShippedEvent e:
                // Notify team in Slack
                await _slackService.NotifyAsync($"Order {e.OrderId} shipped: {e.TrackingNumber}", ct);
                break;
        }
    }
}
// --- end snippet ---

// --- snippet: "Handling Missing Data" ---
public sealed record CustomerSummary(string CustomerId, int OrderCount, decimal TotalRevenue);

// Current is null until the first event for the customer arrives
public class CustomerSummaryProjection : Projection<CustomerSummary?>
{
    protected override CustomerSummary? Apply(CustomerSummary? current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            // Customer not yet created? Create it
            OrderPlacedEvent e when current is null =>
                new CustomerSummary(e.CustomerId, 1, e.Total),

            OrderPlacedEvent e => current with
            {
                OrderCount = current.OrderCount + 1,
                TotalRevenue = current.TotalRevenue + e.Total
            },

            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "At-Least-Once Processing" ---
public sealed record OrderStatus(string Status, long LastProcessedPosition);

public class OrderStatusProjection : Projection<OrderStatus>
{
    public OrderStatusProjection()
    {
        Current = new OrderStatus("New", 0);
    }

    // Idempotent: an event at or before the last processed position is a duplicate
    protected override OrderStatus Apply(OrderStatus current, EventEnvelope @event)
    {
        // Check if we already processed this event
        if (current.LastProcessedPosition >= @event.Position.Value)
            return current;  // Already processed

        // Process event
        var updated = @event.Event switch
        {
            OrderPlacedEvent => current with { Status = "Placed" },
            OrderShippedEvent => current with { Status = "Shipped" },
            OrderDeliveredEvent => current with { Status = "Delivered" },
            _ => current
        };

        return updated with { LastProcessedPosition = @event.Position.Value };
    }
}
// --- end snippet ---

// --- snippet: "Rebuilding from Scratch" ---
public class ProjectionRebuildService
{
    private readonly IEventStore _eventStore;

    public ProjectionRebuildService(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public async Task<TState> RebuildAsync<TState>(
        Projection<TState> projection,
        StreamId streamId,
        CancellationToken ct = default)
    {
        // Replay all events of the stream; StreamId.Global replays every stream
        await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            await projection.HandleAsync(envelope, ct);
        }

        return projection.Current;
    }
}
// --- end snippet ---

// --- snippet: "Partial Rebuilds" ---
public class OrderProjectionRebuilder
{
    private readonly IEventStore _eventStore;
    private readonly IOrderDetailsRepository _detailsRepository;

    public OrderProjectionRebuilder(IEventStore eventStore, IOrderDetailsRepository detailsRepository)
    {
        _eventStore = eventStore;
        _detailsRepository = detailsRepository;
    }

    public async Task RebuildOrderProjection(string orderId, CancellationToken ct = default)
    {
        var streamId = new StreamId($"order-{orderId}");
        var projection = new OrderDetailsProjection();

        await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            await projection.HandleAsync(envelope, ct);
        }

        await _detailsRepository.SaveAsync(projection.Current, ct);
    }
}
// --- end snippet ---

// --- snippet: "Projection Chains" ---
public sealed record CustomerSatisfaction(int RecentDeliveries, int RecentReturns);

// Projection 2: Track customer satisfaction next to OrderStatusProjection
public class CustomerSatisfactionProjection : Projection<CustomerSatisfaction>
{
    public CustomerSatisfactionProjection()
    {
        Current = new CustomerSatisfaction(0, 0);
    }

    protected override CustomerSatisfaction Apply(
        CustomerSatisfaction current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderDeliveredEvent =>
                current with { RecentDeliveries = current.RecentDeliveries + 1 },

            OrderReturnRequestedEvent =>
                current with { RecentReturns = current.RecentReturns + 1 },

            _ => current
        };
    }
}
// --- end snippet ---

// --- snippet: "Example 1: Order List with Search" ---
public record OrderListItem(
    string OrderId,
    string CustomerId,
    decimal Total,
    string Status,
    DateTimeOffset PlacedAt);

public class OrderListProjection : Projection<ImmutableList<OrderListItem>>
{
    public OrderListProjection()
    {
        Current = ImmutableList<OrderListItem>.Empty;
    }

    protected override ImmutableList<OrderListItem> Apply(
        ImmutableList<OrderListItem> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => current.Add(new OrderListItem(
                e.OrderId, e.CustomerId, e.Total, "Placed", e.PlacedAt)),

            OrderShippedEvent e => current.Select(item =>
                item.OrderId == e.OrderId
                    ? item with { Status = "Shipped" }
                    : item
            ).ToImmutableList(),

            OrderCancelledEvent e => current.RemoveAll(item => item.OrderId == e.OrderId),

            _ => current
        };
    }
}

public class OrderListSearchService
{
    private readonly OrderListProjection _projection;

    public OrderListSearchService(OrderListProjection projection)
    {
        _projection = projection;
    }

    public List<OrderListItem> SearchByCustomer(string customerId)
    {
        return _projection.Current
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.PlacedAt)
            .ToList();
    }

    public List<OrderListItem> SearchByStatus(string status)
    {
        return _projection.Current
            .Where(o => o.Status == status)
            .ToList();
    }

    public decimal GetCustomerTotalRevenue(string customerId)
    {
        return _projection.Current
            .Where(o => o.CustomerId == customerId)
            .Sum(o => o.Total);
    }
}
// --- end snippet ---

// --- snippet: "Example 2: Customer Order Statistics" ---
public record CustomerOrderStatistics(
    string CustomerId,
    int OrderCount,
    decimal TotalRevenue);

public class CustomerOrderStatisticsProjection
    : Projection<ImmutableDictionary<string, CustomerOrderStatistics>>
{
    public CustomerOrderStatisticsProjection()
    {
        Current = ImmutableDictionary<string, CustomerOrderStatistics>.Empty;
    }

    protected override ImmutableDictionary<string, CustomerOrderStatistics> Apply(
        ImmutableDictionary<string, CustomerOrderStatistics> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => current.SetItem(e.CustomerId, AddOrder(current, e)),
            _ => current
        };
    }

    private static CustomerOrderStatistics AddOrder(
        ImmutableDictionary<string, CustomerOrderStatistics> current,
        OrderPlacedEvent e)
    {
        var stat = current.GetValueOrDefault(e.CustomerId) ?? new(e.CustomerId, 0, 0m);
        return stat with
        {
            OrderCount = stat.OrderCount + 1,
            TotalRevenue = stat.TotalRevenue + e.Total
        };
    }
}
// --- end snippet ---

// --- snippet: "Fault-Tolerant Projection Processor" ---
public class ResilientProjectionProcessor<TState>
{
    private const int MaxRetries = 3;

    private readonly Projection<TState> _projection;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly ILogger _logger;

    public ResilientProjectionProcessor(
        Projection<TState> projection,
        IDeadLetterStore deadLetterStore,
        ILogger logger)
    {
        _projection = projection;
        _deadLetterStore = deadLetterStore;
        _logger = logger;
    }

    public async Task ProcessAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _projection.HandleAsync(envelope, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Projection failed for {EventType}", envelope.Event.GetType().Name);

                if (attempt == MaxRetries)
                {
                    _logger.LogError("Max retries exceeded for {Position}", envelope.Position.Value);
                    // Dead letter: save failed event for manual review
                    await _deadLetterStore.WriteAsync("order-projection", envelope, ex, ct);
                    return;
                }

                // Retry with exponential backoff: 200 ms, 400 ms, 800 ms
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, attempt + 1) * 100), ct);
            }
        }
    }
}
// --- end snippet ---
