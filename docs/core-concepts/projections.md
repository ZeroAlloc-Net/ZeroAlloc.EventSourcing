# Core Concepts: Projections

A projection is a read model derived from events. While aggregates are optimized for commands (writes), projections are optimized for queries (reads). They answer questions like "Show me all orders by customer" or "How much revenue did we make this month?"

## What is a Projection?

A projection is an automatically-updated view of your data, built by processing events. Instead of querying aggregates directly, you query projections, which are denormalized specifically for your queries.

**Conceptually:**

```
Events (source of truth):
  Order-123: [OrderPlaced, Confirmed, Shipped, Refunded]
  Order-456: [OrderPlaced, Cancelled]

Projection: Customer Summary
  Customer-789:
    - Total Orders: 2
    - Total Revenue: $2,500
    - Pending Orders: 0

Projection: Orders by Status
  Shipped: [Order-123]
  Cancelled: [Order-456]
```

Projections are:
- **Derived** — Built from events, never stored directly
- **Denormalized** — Organized for query efficiency
- **Rebuilding** — Can be reconstructed by replaying events
- **Eventually consistent** — Updated asynchronously as events arrive

## Single-Stream vs. Multi-Stream Projections

### Single-Stream Projections

Project data from a single aggregate's events:

```csharp
// Project a single Order's events into a read model
public class OrderDetailsProjection : Projection<OrderDetails>
{
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
                ShippedAt = DateTime.UtcNow
            },
            _ => current
        };
    }
}

// Usage: Load a single order's projection
var projection = new OrderDetailsProjection();
await foreach (var envelope in eventStore.ReadAsync(orderId, StreamPosition.Start))
{
    await projection.HandleAsync(envelope);
}

var orderDetails = projection.Current;
```

Use single-stream projections for:
- Detailed aggregate views
- Audit reports for a single entity
- Rich display models for a specific entity

### Multi-Stream Projections

Aggregate data across multiple aggregates:

```csharp
// Project all orders into a customer summary
public record CustomerOrderSummary(
    string CustomerId,
    int OrderCount,
    decimal TotalRevenue,
    List<string> RecentOrderIds
);

public class CustomerSummaryProjection : Projection<CustomerOrderSummary>
{
    private readonly string _customerId;

    public CustomerSummaryProjection(string customerId)
    {
        _customerId = customerId;
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
                    RecentOrderIds = new(current.RecentOrderIds) { e.OrderId }.Take(10).ToList()
                },
            OrderCancelledEvent e =>
                current with
                {
                    TotalRevenue = current.TotalRevenue - e.CancelledAmount
                },
            _ => current
        };
    }
}

// Usage: Scan all order events and aggregate by customer
var customerSummaries = new Dictionary<string, CustomerSummaryProjection>();

// Read the $all stream (all events in the system)
await foreach (var envelope in eventStore.ReadAsync(new StreamId("$all"), StreamPosition.Start))
{
    if (envelope.Event is OrderPlacedEvent ope)
    {
        var customerId = ope.CustomerId;
        if (!customerSummaries.ContainsKey(customerId))
            customerSummaries[customerId] = new CustomerSummaryProjection(customerId);
        
        await customerSummaries[customerId].HandleAsync(envelope);
    }
}
```

Use multi-stream projections for:
- Aggregated reports (revenue by customer, inventory counts)
- Cross-aggregate summaries
- Dashboards and analytics
- Search indices

## Eventual Consistency in Projections

Projections are eventually consistent: they may lag behind events, but will eventually reflect the complete state.

```
Event Store Timeline:
  T1: OrderPlaced
  T2: OrderConfirmed
  T3: OrderShipped

Projection Timeline:
  T1+1ms: OrderPlaced processed, status = "Placed"
  T2+2ms: OrderConfirmed processed, status = "Confirmed"
  T3+5ms: OrderShipped processed, status = "Shipped"

Query at T3+3ms: Might see "Confirmed" (before OrderShipped arrives)
Query at T3+10ms: Sees "Shipped" (eventually consistent)
```

**Why eventual consistency?**

- **Decoupling** — Read models don't block writes
- **Scalability** — Can process events asynchronously
- **Resilience** — If projection is slow, events aren't lost
- **Flexibility** — Different projections at different speeds

**Mitigating eventual consistency:**

```csharp
// 1. Polling for consistency
async ValueTask WaitForProjectionCatchUp(
    IProjectionStore projectionStore,
    string projectionName,
    StreamPosition targetPosition,
    TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var projectionVersion = await projectionStore.GetVersionAsync(projectionName);
        if (projectionVersion >= targetPosition)
            return;  // Caught up
        
        await Task.Delay(100);
    }
    
    throw new TimeoutException($"Projection did not catch up within {timeout}");
}

// 2. Include position in events
// When returning an event, also return its position
public record OrderProjectionResult(OrderDetails Details, StreamPosition Position);

// 3. Accept eventual consistency
// For most UIs, small delays are acceptable
```

## Projection Patterns

### Materialized Views

Store the projection in a database for efficient querying:

```csharp
public class OrdersDataStore  // Database table
{
    public string OrderId { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; }
}

public class OrdersMaterializationProjection : Projection<Dictionary<string, OrdersDataStore>>
{
    private readonly IOrderRepository _repository;

    public OrdersMaterializationProjection(IOrderRepository repository)
    {
        _repository = repository;
    }

    protected override Dictionary<string, OrdersDataStore> Apply(
        Dictionary<string, OrdersDataStore> current,
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

    private static Dictionary<string, OrdersDataStore> ApplyOrderUpdate(
        Dictionary<string, OrdersDataStore> current,
        string orderId,
        Func<OrdersDataStore, OrdersDataStore> update)
    {
        var updated = new Dictionary<string, OrdersDataStore>(current);
        if (updated.TryGetValue(orderId, out var row))
        {
            updated[orderId] = update(row);
        }
        else
        {
            updated[orderId] = update(new OrdersDataStore { OrderId = orderId });
        }
        return updated;
    }

    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        await base.HandleAsync(@event, ct);
        // Persist to database after each event
        foreach (var order in Current.Values)
        {
            await _repository.SaveAsync(order, ct);
        }
    }
}
```

### Counts and Aggregations

Count or sum events:

```csharp
public record RevenueByMonth(Dictionary<string, decimal> ByMonth);

public class RevenueProjection : Projection<RevenueByMonth>
{
    protected override RevenueByMonth Apply(RevenueByMonth current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current with
                {
                    ByMonth = current.ByMonth.Update(
                        key: DateTime.UtcNow.ToString("yyyy-MM"),
                        add: e.Total)
                },
            OrderCancelledEvent e =>
                current with
                {
                    ByMonth = current.ByMonth.Update(
                        key: DateTime.UtcNow.ToString("yyyy-MM"),
                        subtract: e.Amount)
                },
            _ => current
        };
    }
}
```

### Search Indices

Build searchable indices:

```csharp
public class OrderSearchIndexProjection : Projection<SearchIndex>
{
    protected override SearchIndex Apply(SearchIndex current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.Add(new SearchDocument
                {
                    OrderId = e.OrderId,
                    CustomerName = e.CustomerName,
                    Total = e.Total,
                    Keywords = new[] { e.OrderId, e.CustomerName }
                }),
            _ => current
        };
    }
}

// Query the search index
var results = searchIndex.Search("customer name");
```

## IProjection Interface

ZeroAlloc.EventSourcing provides a base class:

```csharp
public abstract class Projection<TReadModel>
{
    // The current read model state
    public TReadModel Current { get; protected set; }

    // Apply an event to the read model
    protected abstract TReadModel Apply(TReadModel current, EventEnvelope @event);

    // Handle an inbound event
    public virtual async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Current = Apply(Current, @event);
        await ValueTask.CompletedTask;
    }
}
```

**Implementing a projection:**

```csharp
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
            OrderPlacedEvent e => current with { Status = "Placed", Total = e.Total },
            OrderShippedEvent e => current with { Status = "Shipped", TrackingNumber = e.TrackingNumber },
            _ => current
        };
    }
}
```

## Projection Rebuilding and Replay

Projections can be rebuilt from events. This is useful when:
- The projection logic changes
- The underlying data model changes
- You need to fix corrupted projections

```csharp
// Rebuild a projection by replaying all events
public async Task RebuildProjectionAsync(
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
```

**Rebuild without downtime:**

```csharp
// 1. Create a new projection instance
var newProjection = new OrderProjection();

// 2. Rebuild it in the background
var rebuildTask = RebuildProjectionAsync(eventStore, streamId, newProjection);

// 3. Continue serving from old projection
var currentResults = oldProjection.Current;

// 4. Once rebuild completes, switch
await rebuildTask;
SwapProjection(oldProjection, newProjection);
```

## Consistency Guarantees

Projections provide eventual consistency with at-least-once event delivery:

**At-least-once delivery:**
- Events are delivered to projections at least once
- An event might be processed twice in rare cases (network retries, crashes)
- Projections should be idempotent (applying the same event twice produces the same result)

```csharp
// Idempotent projection
public override OrderData Apply(OrderData current, EventEnvelope @event)
{
    return @event.Event switch
    {
        OrderPlacedEvent e =>
            // Safe to apply twice: 'with' creates new state regardless
            current with { OrderId = e.OrderId, Total = e.Total },
        _ => current
    };
}
```

**Event deduplication (optional):**

```csharp
// If exactly-once is critical, track processed event positions
public class TrackingProjection : Projection<(OrderData Data, StreamPosition LastPosition)>
{
    protected override (OrderData, StreamPosition) Apply(
        (OrderData, StreamPosition) current,
        EventEnvelope @event)
    {
        // Skip if we've already processed this position
        if (current.LastPosition >= @event.Position)
            return current;

        var newData = ApplyEvent(current.Data, @event.Event);
        return (newData, @event.Position);
    }
}
```

## Use Cases

### Real-Time Analytics Dashboard

```csharp
// Project real-time order metrics
var dashboardProjection = new OrderMetricsProjection();

// Subscribe to new events
var subscription = await eventStore.SubscribeAsync(
    streamId: new StreamId("$all"),
    from: StreamPosition.Start,
    handler: async (envelope, ct) =>
    {
        await dashboardProjection.HandleAsync(envelope, ct);
        // Dashboard updates in real-time
    }
);
```

### API Query Layer

```csharp
// Project into a queryable data store
var queryProjection = new OrderQueryProjection(database);

// Rebuild when needed
await RebuildProjectionAsync(eventStore, streamId, queryProjection);

// Expose via API
app.MapGet("/orders", () => queryProjection.Current.Orders);
```

### Event-Driven Notifications

```csharp
// Project to send notifications
public class NotificationProjection : Projection<List<Notification>>
{
    private readonly INotificationService _notificationService;

    protected override List<Notification> Apply(List<Notification> current, EventEnvelope @event)
    {
        var notification = @event.Event switch
        {
            OrderShippedEvent e => new Notification($"Order {e.OrderId} shipped with {e.TrackingNumber}"),
            OrderDeliveredEvent e => new Notification($"Order {e.OrderId} delivered"),
            _ => null
        };

        if (notification != null)
        {
            current.Add(notification);
            _ = _notificationService.SendAsync(notification);  // Fire and forget
        }

        return current;
    }
}
```

### Cross-Aggregate Relationships

```csharp
// Project customer-order relationships
public class CustomerOrderGraphProjection : Projection<Dictionary<string, List<string>>>
{
    protected override Dictionary<string, List<string>> Apply(
        Dictionary<string, List<string>> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.Update(e.CustomerId, () => new())
                    .With(orders => orders.Add(e.OrderId)),
            _ => current
        };
    }
}
```

## Integration with Repositories and Queries

Projections complement aggregates:

```csharp
// Aggregate: Single order details and behavior
var order = await repository.LoadAsync(orderId);
order.Ship("TRACK-123");
await repository.SaveAsync(order);

// Projection: List all orders, search, filter
var allOrders = orderProjection.Current;
var shippedOrders = allOrders.Where(o => o.Status == "Shipped");

// Projection: Customer summary across many orders
var customerSummary = customerProjection.Current;
var revenue = customerSummary.TotalRevenue;
```

## Summary

Projections are:
- **Read models** — Derived views of aggregates, optimized for queries
- **Single-stream or multi-stream** — Per-aggregate or aggregated across many
- **Eventually consistent** — Updated asynchronously, lag is expected
- **Rebuilding** — Can be reconstructed by replaying events
- **At-least-once delivery** — Events processed at least once (idempotency needed)
- **Decoupled** — Don't block writes, independent from aggregates
- **Flexible** — Materialized views, counts, search indices, notifications

Projections free you from the polyglot persistence problem: events are the source of truth, but you can derive any read model you need.

## Next Steps

- **[Core Concepts: Architecture](./architecture.md)** — How all concepts fit together
- **[Usage Guide: Advanced Projections](../usage-guides/projections.md)** — Complex projection patterns
- **[Example: Real-Time Analytics](../examples/realtime-analytics.md)** — End-to-end projection example
