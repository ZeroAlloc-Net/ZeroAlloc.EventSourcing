# Usage Guide: Projections Usage

## Scenario: How Do I Query My Event-Sourced Data Efficiently?

Projections are read models derived from events. While aggregates optimize for commands, projections optimize for queries. This guide shows how to build fast, queryable views of your event-sourced data.

## Projections as Read Models

Aggregates are optimized for commands (writes), but reading them is expensive—you have to replay all their events. Projections solve this by maintaining denormalized views.

**Conceptually:**

```
Event Stream (write model):
  Order-123: [OrderPlaced, Confirmed, Shipped, Delivered, Refunded]

Projection 1: OrderListView (what orders exist?)
  {OrderId: 123, Status: "Refunded", Total: 1500, CustomerId: 456}

Projection 2: CustomerOrderCount (how many orders per customer?)
  {CustomerId: 456, OrderCount: 5, TotalRevenue: 7500}

Projection 3: OrdersByStatus (group orders by status)
  {Status: "Refunded", Orders: [123, ...]}
```

**Benefits:**
- **Fast queries** — Projections are indexed, queried directly (no replay)
- **Denormalized** — Data organized for specific query patterns
- **Rebuilding** — Reconstruct by replaying events
- **Decoupled** — Update aggregates without changing query logic

## Single-Stream vs. Multi-Stream Projections

### Single-Stream Projections

Project events from a single aggregate's stream:

```csharp
// Project a single Order's events
public class OrderDetailsProjection : Projection<OrderDetails>
{
    public override OrderDetails Apply(OrderDetails current, EventEnvelope @event)
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

// Usage
var projection = new OrderDetailsProjection();
await foreach (var envelope in eventStore.ReadAsync(orderId, StreamPosition.Start))
{
    projection.Apply(envelope);
}

var orderDetails = projection.Current;
Console.WriteLine($"Order {orderDetails.OrderId}: {orderDetails.Status}");
```

**Use for:**
- Detailed order/customer views
- Single-aggregate audit reports
- Rich display models for a specific entity

### Multi-Stream Projections

Aggregate data across multiple aggregates:

```csharp
// Project all orders into a customer summary
public class CustomerOrderSummaryProjection : Projection<CustomerOrderSummary>
{
    private readonly string _customerId;
    
    public CustomerOrderSummaryProjection(string customerId)
    {
        _customerId = customerId;
    }
    
    public override CustomerOrderSummary Apply(
        CustomerOrderSummary current,
        EventEnvelope @event)
    {
        // Only process events from this customer's orders
        if (@event.Event is not OrderPlacedEvent orderEvent
            || orderEvent.CustomerId != _customerId)
        {
            return current;
        }
        
        return @event.Event switch
        {
            OrderPlacedEvent e => current with
            {
                CustomerId = _customerId,
                OrderCount = current.OrderCount + 1,
                TotalRevenue = current.TotalRevenue + e.Total,
                RecentOrderIds = current.RecentOrderIds
                    .Prepend(e.OrderId)
                    .Take(10)
                    .ToList()
            },
            OrderRefundedEvent e => current with
            {
                TotalRevenue = current.TotalRevenue - e.RefundAmount
            },
            _ => current
        };
    }
}

// Usage: Process all events from all streams
var projection = new CustomerOrderSummaryProjection("customer-456");
await foreach (var envelope in eventStore.ReadAllAsync())
{
    projection.Apply(envelope);
}

var summary = projection.Current;
Console.WriteLine($"Customer {summary.CustomerId}: {summary.OrderCount} orders, ${summary.TotalRevenue}");
```

**Use for:**
- Aggregated statistics across aggregates
- Search indices (e.g., "orders by customer")
- Analytics and reporting
- Cross-entity queries

## Projection Patterns

### Pattern 1: Materialization (Denormalized View)

Store projection result in a database for fast querying:

```csharp
public class OrderDetailsProjection : Projection<OrderDetails>
{
    private readonly IOrderDetailsRepository _repository;
    
    public override OrderDetails Apply(OrderDetails current, EventEnvelope @event)
    {
        var updated = @event.Event switch
        {
            OrderPlacedEvent e => current with { Status = "Placed", Total = e.Total },
            OrderShippedEvent e => current with { Status = "Shipped", Tracking = e.Tracking },
            _ => current
        };
        
        // Persist to read model
        _repository.Save(updated);
        
        return updated;
    }
}

// Query the materialized view (fast, no replay)
var order = await _orderRepository.GetByIdAsync(orderId);
Console.WriteLine($"Status: {order.Status}");
```

### Pattern 2: Counts and Aggregations

Track counts and statistics:

```csharp
public record CustomerOrderCount(string CustomerId, int Count);

public class CustomerOrderCountProjection : Projection<Dictionary<string, int>>
{
    public override Dictionary<string, int> Apply(
        Dictionary<string, int> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.TryGetValue(e.CustomerId, out var count)
                    ? current.Update(e.CustomerId, count + 1)
                    : current.Add(e.CustomerId, 1),
            
            OrderCancelledEvent e =>
                current.TryGetValue(e.CustomerId, out var count)
                    ? current.Update(e.CustomerId, count - 1)
                    : current,
            
            _ => current
        };
    }
}

// Usage
var projection = new CustomerOrderCountProjection();
await foreach (var envelope in eventStore.ReadAllAsync())
{
    projection.Apply(envelope);
}

var counts = projection.Current;
var customerId = "cust-123";
if (counts.TryGetValue(customerId, out var count))
{
    Console.WriteLine($"Customer has {count} orders");
}
```

### Pattern 3: Search Indices

Build searchable indices:

```csharp
public record OrderIndex(string OrderId, string CustomerId, decimal Total, string Status);

public class OrderSearchProjection : Projection<List<OrderIndex>>
{
    public override List<OrderIndex> Apply(List<OrderIndex> current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
                current.Append(new OrderIndex(e.OrderId, e.CustomerId, e.Total, "Placed"))
                    .ToList(),
            
            OrderShippedEvent e =>
                current.Select(o =>
                    o.OrderId == e.OrderId
                        ? o with { Status = "Shipped" }
                        : o
                ).ToList(),
            
            _ => current
        };
    }
}

// Usage: Search index allows quick lookups
public class OrderSearchService
{
    private OrderSearchProjection _projection;
    
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
```

### Pattern 4: Notifications and Side Effects

Trigger actions when events arrive:

```csharp
public class OrderNotificationProjection : Projection<object>
{
    private readonly IEmailService _emailService;
    private readonly ISlackService _slackService;
    
    public OrderNotificationProjection(IEmailService email, ISlackService slack)
    {
        _emailService = email;
        _slackService = slack;
    }
    
    public override object Apply(object current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
            {
                // Send confirmation email
                _emailService.SendOrderPlaced(e.OrderId, e.CustomerId);
                return current;
            },
            
            OrderShippedEvent e =>
            {
                // Notify team in Slack
                _slackService.Notify($"Order {e.OrderId} shipped: {e.TrackingNumber}");
                return current;
            },
            
            _ => current
        };
    }
}
```

## IProjection Interface

ZeroAlloc.EventSourcing defines the projection interface:

```csharp
public interface IProjection<TState>
{
    TState Current { get; }
    Task HandleAsync(EventEnvelope @event);
}
```

### Implementing IProjection

```csharp
public class OrderDetailsProjection : IProjection<OrderDetails>
{
    private OrderDetails _current = new();
    
    public OrderDetails Current => _current;
    
    public Task HandleAsync(EventEnvelope @event)
    {
        _current = @event.Event switch
        {
            OrderPlacedEvent e => _current with
            {
                OrderId = e.OrderId,
                Status = "Placed",
                Total = e.Total
            },
            OrderShippedEvent e => _current with
            {
                Status = "Shipped",
                TrackingNumber = e.TrackingNumber
            },
            _ => _current
        };
        
        return Task.CompletedTask;
    }
}

// Usage
var projection = new OrderDetailsProjection();
await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
{
    await projection.HandleAsync(envelope);
}

var details = projection.Current;
```

## Applying Events to Projections

### Handling Events

Process each event type:

```csharp
public override OrderListItem Apply(OrderListItem current, EventEnvelope @event)
{
    return @event.Event switch
    {
        OrderPlacedEvent e => new OrderListItem
        {
            OrderId = e.OrderId,
            CustomerId = e.CustomerId,
            Total = e.Total,
            Status = "Placed",
            PlacedAt = e.PlacedAt
        },
        
        OrderConfirmedEvent e => current with
        {
            Status = "Confirmed"
        },
        
        OrderShippedEvent e => current with
        {
            Status = "Shipped",
            TrackingNumber = e.TrackingNumber
        },
        
        OrderCancelledEvent e => current with
        {
            Status = "Cancelled"
        },
        
        _ => current  // Unknown events
    };
}
```

### Handling Missing Data

Gracefully handle incomplete state:

```csharp
public override CustomerSummary Apply(CustomerSummary current, EventEnvelope @event)
{
    return @event.Event switch
    {
        OrderPlacedEvent e =>
        {
            // Customer not yet created? Create it
            if (current?.CustomerId == null)
            {
                current = new CustomerSummary { CustomerId = e.CustomerId };
            }
            
            return current with
            {
                OrderCount = (current.OrderCount ?? 0) + 1,
                TotalRevenue = (current.TotalRevenue ?? 0) + e.Total
            };
        },
        
        _ => current
    };
}
```

## Consistency Guarantees

### Eventual Consistency

Projections are eventually consistent—they lag behind the event stream:

```
Time 0:  OrderPlacedEvent appended to store
Time 1:  Projection processes event (lag = 1ms)
Time 5:  Projection query returns consistent data

Between Time 0-1: Projection hasn't processed event yet
After Time 1:     Projection shows new data
```

**Implications:**
- Queries may show stale data briefly
- Acceptable for most applications
- Not suitable for financial transactions requiring immediate consistency

### At-Least-Once Processing

Events are processed at least once, possibly multiple times:

```csharp
// Projection might be invoked twice for the same event
projection.Apply(envelope);  // First time
projection.Apply(envelope);  // Duplicate?

// Solution: Make Apply idempotent
public override OrderStatus Apply(OrderStatus current, EventEnvelope @event)
{
    // Check if we already processed this event
    if (current.LastProcessedPosition >= @event.Position.Value)
        return current;  // Already processed
    
    // Process event
    var updated = @event.Event switch { ... };
    
    return updated with { LastProcessedPosition = @event.Position.Value };
}
```

## Projection Rebuilding and Replays

### Rebuilding from Scratch

Reconstruct a projection by replaying all events:

```csharp
public class ProjectionRebuildService<TState>
{
    private readonly IEventStore _eventStore;
    
    public async Task<TState> Rebuild(IProjection<TState> projection)
    {
        // Replay all events
        await foreach (var envelope in _eventStore.ReadAllAsync())
        {
            await projection.HandleAsync(envelope);
        }
        
        return projection.Current;
    }
}

// Usage
var projection = new OrderListProjection();
var finalState = await rebuildService.Rebuild(projection);
Console.WriteLine($"Rebuilt with {finalState.Count} orders");
```

### Partial Rebuilds

Rebuild a single stream's projection:

```csharp
public async Task RebuildOrderProjection(OrderId orderId)
{
    var streamId = new StreamId($"order-{orderId.Value}");
    var projection = new OrderDetailsProjection();
    
    await foreach (var envelope in _eventStore.ReadAsync(streamId, StreamPosition.Start))
    {
        await projection.HandleAsync(envelope);
    }
    
    var details = projection.Current;
    await _repository.SaveAsync(details);
}
```

## Multi-Step Workflows with Projections

### Projection Chains

Use projections to orchestrate workflows:

```csharp
// Projection 1: Track order status
public class OrderStatusProjection : Projection<OrderStatus> { ... }

// Projection 2: Track customer satisfaction (depends on OrderStatus)
public class CustomerSatisfactionProjection : Projection<CustomerSatisfaction>
{
    public override CustomerSatisfaction Apply(
        CustomerSatisfaction current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderDeliveredEvent e =>
                current with { RecentDeliveries = current.RecentDeliveries + 1 },
            
            OrderReturnRequestedEvent e =>
                current with { RecentReturns = current.RecentReturns + 1 },
            
            _ => current
        };
    }
}

// Usage: Apply both in sequence
var statusProj = new OrderStatusProjection();
var satisfactionProj = new CustomerSatisfactionProjection();

await foreach (var envelope in _eventStore.ReadAllAsync())
{
    await statusProj.HandleAsync(envelope);
    await satisfactionProj.HandleAsync(envelope);
}
```

## Testing Projections

### Test Event Application

```csharp
[Fact]
public void Projection_AppliesOrderPlacedEvent()
{
    // Arrange
    var projection = new OrderListProjection();
    var @event = new OrderPlacedEvent("ORD-001", "cust-123", 1500m, DateTime.Now);
    var envelope = new EventEnvelope { Event = @event, Position = new StreamPosition(1) };
    
    // Act
    var result = projection.Apply(projection.Current, envelope);
    
    // Assert
    Assert.NotNull(result);
    Assert.Contains("ORD-001", result.OrderIds);
}

[Fact]
public void Projection_UpdatesExistingItem()
{
    // Arrange
    var projection = new OrderListProjection();
    var current = new OrderList { Items = new[] { new OrderItem { OrderId = "ORD-001" } } };
    
    var @event = new OrderShippedEvent("ORD-001", "TRACK-123");
    var envelope = new EventEnvelope { Event = @event, Position = new StreamPosition(2) };
    
    // Act
    var result = projection.Apply(current, envelope);
    
    // Assert
    var item = result.Items.Single(i => i.OrderId == "ORD-001");
    Assert.Equal("Shipped", item.Status);
}
```

### Test Multi-Stream Projection

```csharp
[Fact]
public async Task CustomerSummary_AggregatesAllOrders()
{
    // Arrange
    var projection = new CustomerOrderSummaryProjection("cust-123");
    
    var events = new[]
    {
        new EventEnvelope
        {
            Event = new OrderPlacedEvent("ORD-001", "cust-123", 1000m, DateTime.Now),
            Position = new StreamPosition(1)
        },
        new EventEnvelope
        {
            Event = new OrderPlacedEvent("ORD-002", "cust-123", 500m, DateTime.Now),
            Position = new StreamPosition(2)
        },
        new EventEnvelope
        {
            Event = new OrderPlacedEvent("ORD-003", "cust-456", 200m, DateTime.Now),
            Position = new StreamPosition(3)
        }
    };
    
    // Act
    foreach (var envelope in events)
    {
        await projection.HandleAsync(envelope);
    }
    
    // Assert: Only customer-123 orders counted
    Assert.Equal(2, projection.Current.OrderCount);
    Assert.Equal(1500m, projection.Current.TotalRevenue);
}
```

## Complete Examples

### Example 1: Order List with Search

```csharp
public record OrderListItem(
    string OrderId,
    string CustomerId,
    decimal Total,
    string Status,
    DateTime PlacedAt);

public class OrderListProjection : Projection<List<OrderListItem>>
{
    private List<OrderListItem> _items = new();
    
    public override List<OrderListItem> Apply(
        List<OrderListItem> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => current.Append(new OrderListItem(
                e.OrderId, e.CustomerId, e.Total, "Placed", e.PlacedAt
            )).ToList(),
            
            OrderShippedEvent e => current.Select(item =>
                item.OrderId == e.OrderId
                    ? item with { Status = "Shipped" }
                    : item
            ).ToList(),
            
            OrderCancelledEvent e => current.Where(item =>
                item.OrderId != e.OrderId
            ).ToList(),
            
            _ => current
        };
    }
}

public class OrderSearchService
{
    private readonly OrderListProjection _projection;
    
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
```

### Example 2: Customer Order Count

```csharp
public record CustomerOrderStatistics(
    string CustomerId,
    int OrderCount,
    decimal TotalRevenue,
    string MostCommonStatus);

public class CustomerOrderCountProjection : Projection<Dictionary<string, CustomerOrderStatistics>>
{
    private Dictionary<string, CustomerOrderStatistics> _stats = new();
    
    public override Dictionary<string, CustomerOrderStatistics> Apply(
        Dictionary<string, CustomerOrderStatistics> current,
        EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e =>
            {
                if (!current.TryGetValue(e.CustomerId, out var stat))
                {
                    stat = new(e.CustomerId, 0, 0, "Placed");
                }
                
                return current.Update(e.CustomerId,
                    stat with
                    {
                        OrderCount = stat.OrderCount + 1,
                        TotalRevenue = stat.TotalRevenue + e.Total
                    });
            },
            
            _ => current
        };
    }
}
```

## Performance: Query Speed vs. Update Latency

### Query Optimization

Materialized projections are optimized for queries:

```
Direct Query (no projection):
  1. Load aggregate: ~100ms (replay 1000 events)
  2. Extract info: ~1ms
  Total: ~101ms per query

With Materialized Projection:
  1. Query projection: ~1ms (indexed database query)
  Total: ~1ms per query

Improvement: 100x faster!
```

### Update Latency

Projections may lag behind events:

```
Event appended: T+0
Projection processed: T+50ms
Query returns updated data: T+50ms
```

For most applications, 50-100ms lag is acceptable. For real-time dashboards, use subscriptions.

## Handling Projection Failures and Retries

### Fault-Tolerant Projection Processor

```csharp
public class ResilientProjectionProcessor<TState>
{
    private readonly IProjection<TState> _projection;
    private readonly ILogger<ResilientProjectionProcessor<TState>> _logger;
    private int _retryCount = 0;
    private const int MaxRetries = 3;
    
    public async Task Process(EventEnvelope envelope)
    {
        try
        {
            await _projection.HandleAsync(envelope);
            _retryCount = 0;  // Reset on success
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Projection failed for {EventType}", @envelope.Event?.GetType().Name);
            
            if (_retryCount < MaxRetries)
            {
                _retryCount++;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, _retryCount) * 100));
                await Process(envelope);  // Retry with exponential backoff
            }
            else
            {
                _logger.LogError("Max retries exceeded for {Position}", envelope.Position.Value);
                // Dead letter: save failed event for manual review
                await DeadLetterEvent(envelope);
            }
        }
    }
    
    private async Task DeadLetterEvent(EventEnvelope envelope)
    {
        // Save to database for manual review
        await _deadLetterStore.SaveAsync(envelope);
    }
}
```

## Summary

Effective projection usage requires:

1. **Identify query patterns** — What questions do users ask?
2. **Design appropriate projections** — Single-stream or multi-stream?
3. **Implement idempotent handlers** — Handle duplicate event processing
4. **Persist materialized views** — Index projections for fast queries
5. **Test thoroughly** — Verify correctness across event sequences
6. **Accept eventual consistency** — Projections lag behind events
7. **Plan for rebuilds** — Ability to reconstruct from events

## Next Steps

- **[SQL Adapters](./sql-adapters.md)** — Persist projections to databases
- **[Performance Guide](../performance.md)** — Optimize query patterns
- **[Testing Projections](../testing/)** — Comprehensive projection testing
