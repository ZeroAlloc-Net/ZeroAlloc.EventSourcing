# Advanced Projection Patterns

**Version:** 1.0  
**Last Updated:** 2026-04-04

## Overview

Projections transform events into read models optimized for queries. This guide covers advanced patterns beyond the basics.

## Pattern 1: Filtered Projections

Only process events matching criteria:

```csharp
public class HighValueOrdersProjection : Projection
{
    private readonly Dictionary<OrderId, OrderReadModel> _highValueOrders = new();
    private const decimal HIGH_VALUE_THRESHOLD = 10000m;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        // Only process order events
        if (envelope.Event is not OrderEvent orderEvent)
            return false;

        // Filter on event type and data
        switch (orderEvent)
        {
            case OrderPlacedEvent e when e.Total > HIGH_VALUE_THRESHOLD:
                _highValueOrders[e.OrderId] = new OrderReadModel
                {
                    Id = e.OrderId,
                    Total = e.Total,
                    IsHighValue = true
                };
                return true;

            case OrderCancelledEvent e:
                _highValueOrders.Remove(e.OrderId);
                return true;

            case OrderPlacedEvent:  // Low-value order, ignore
                return false;

            default:
                return false;
        }
    }

    public OrderReadModel? GetHighValueOrder(OrderId id)
    {
        _highValueOrders.TryGetValue(id, out var model);
        return model;
    }
}
```

**Benefits:**
- Smaller read model (only high-value orders)
- Faster queries (filtered data)
- Lower memory usage

## Pattern 2: Composite Projections

Multiple read models from same event stream:

```csharp
/// <summary>
/// One projection can handle multiple read models.
/// Updates all models from single event.
/// </summary>
public class OrderCompositeProjection : Projection
{
    // Read model 1: Total orders by customer
    private readonly Dictionary<CustomerId, int> _orderCountByCustomer = new();

    // Read model 2: Total revenue by customer
    private readonly Dictionary<CustomerId, decimal> _revenueByCustomer = new();

    // Read model 3: Recent orders
    private readonly List<(OrderId, DateTime)> _recentOrders = new();

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case OrderPlacedEvent e:
                // Update all three models
                if (!_orderCountByCustomer.ContainsKey(e.CustomerId))
                    _orderCountByCustomer[e.CustomerId] = 0;
                _orderCountByCustomer[e.CustomerId]++;

                if (!_revenueByCustomer.ContainsKey(e.CustomerId))
                    _revenueByCustomer[e.CustomerId] = 0;
                _revenueByCustomer[e.CustomerId] += e.Total;

                _recentOrders.Add((e.OrderId, DateTime.UtcNow));

                // Keep only last 100 recent orders
                if (_recentOrders.Count > 100)
                    _recentOrders.RemoveAt(0);

                return true;

            case OrderShippedEvent e:
                // Only specific models care about shipping
                return true;

            default:
                return false;
        }
    }

    public int GetOrderCount(CustomerId customerId)
        => _orderCountByCustomer.GetValueOrDefault(customerId, 0);

    public decimal GetTotalRevenue(CustomerId customerId)
        => _revenueByCustomer.GetValueOrDefault(customerId, 0);

    public List<(OrderId, DateTime)> GetRecentOrders()
        => _recentOrders.ToList();
}
```

**Benefits:**
- Single event stream processed once
- Multiple queries from one model
- Consistent snapshot point

## Pattern 3: Stateful Projections

Maintain complex state across events:

```csharp
public class InventoryProjection : Projection
{
    /// <summary>State machine for tracking stock levels.</summary>
    private class InventoryState
    {
        public int OnHand { get; set; }
        public int Reserved { get; set; }
        public int Damaged { get; set; }

        public int Available => OnHand - Reserved - Damaged;
    }

    private readonly Dictionary<ProductId, InventoryState> _inventory = new();

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case StockReceivedEvent e:
                var state = GetOrCreate(e.ProductId);
                state.OnHand += e.Quantity;
                return true;

            case StockReservedEvent e:
                state = GetOrCreate(e.ProductId);
                state.Reserved += e.Quantity;
                // Validation: Can't reserve more than available
                if (state.Reserved > state.OnHand)
                    throw new InvalidOperationException("Insufficient inventory");
                return true;

            case StockReleasedEvent e:
                state = GetOrCreate(e.ProductId);
                state.Reserved -= e.Quantity;
                return true;

            case DamagedStockEvent e:
                state = GetOrCreate(e.ProductId);
                state.Damaged += e.Quantity;
                state.OnHand -= e.Quantity;
                return true;

            default:
                return false;
        }
    }

    private InventoryState GetOrCreate(ProductId productId)
    {
        if (!_inventory.ContainsKey(productId))
            _inventory[productId] = new InventoryState();
        return _inventory[productId];
    }

    public InventoryState? GetInventory(ProductId productId)
    {
        _inventory.TryGetValue(productId, out var state);
        return state;
    }

    public bool CanReserve(ProductId productId, int quantity)
    {
        var state = GetInventory(productId);
        return state != null && state.Available >= quantity;
    }
}
```

**Benefits:**
- Validates invariants (can't reserve more than available)
- Tracks multiple dimensions (on-hand, reserved, damaged)
- Single source of truth for inventory

## Pattern 4: Denormalized Projections

Flatten nested data for efficient queries:

```csharp
/// <summary>
/// Instead of storing normalized Order + LineItems,
/// denormalize into flat table for fast queries.
/// </summary>
public class OrderLineItemProjection : Projection
{
    /// <summary>Flat view: one row per line item.</summary>
    public class LineItemView
    {
        public OrderId OrderId { get; set; }
        public ProductId ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal Total => Quantity * Price;
        public string OrderStatus { get; set; }
    }

    private readonly Dictionary<(OrderId, ProductId), LineItemView> _lineItems = new();

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case OrderPlacedEvent e:
                // Store each line item separately
                foreach (var lineItem in e.LineItems)
                {
                    var key = (e.OrderId, lineItem.ProductId);
                    _lineItems[key] = new LineItemView
                    {
                        OrderId = e.OrderId,
                        ProductId = lineItem.ProductId,
                        Quantity = lineItem.Quantity,
                        Price = lineItem.Price,
                        OrderStatus = "Placed"
                    };
                }
                return true;

            case OrderShippedEvent e:
                // Update status for all line items in this order
                foreach (var key in _lineItems.Keys.Where(k => k.OrderId == e.OrderId))
                {
                    _lineItems[key].OrderStatus = "Shipped";
                }
                return true;

            default:
                return false;
        }
    }

    /// <summary>Query: "All line items for a product, across all orders"</summary>
    public List<LineItemView> GetLineItemsByProduct(ProductId productId)
        => _lineItems.Values.Where(li => li.ProductId == productId).ToList();

    /// <summary>Query: "Total revenue for a product"</summary>
    public decimal GetProductRevenue(ProductId productId)
        => GetLineItemsByProduct(productId).Sum(li => li.Total);

    /// <summary>Query: "All line items in an order"</summary>
    public List<LineItemView> GetOrderLineItems(OrderId orderId)
        => _lineItems.Values.Where(li => li.OrderId == orderId).ToList();
}
```

**Benefits:**
- Optimized for queries (single table, no joins)
- Pre-aggregated data (e.g., total per product)
- Faster than querying events directly

## Pattern 5: Batched Projections

Improve performance with batching:

```csharp
/// <summary>
/// Buffer events and update in batches.
/// Trade-off: durability for performance.
/// </summary>
public class BatchedProjection : Projection
{
    private readonly IProjectionStore _store;
    private readonly List<EventEnvelope> _eventBuffer = new();
    private const int BATCH_SIZE = 100;

    private Dictionary<OrderId, OrderReadModel> _models = new();
    private StreamPosition _position = StreamPosition.Start - 1;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        // Buffer event
        _eventBuffer.Add(envelope);

        // Apply to in-memory model
        ApplyToModel(envelope.Event);

        // Flush batch if full
        if (_eventBuffer.Count >= BATCH_SIZE)
        {
            await FlushAsync();
        }

        return true;
    }

    private void ApplyToModel(object @event)
    {
        if (@event is OrderPlacedEvent ope)
        {
            _models[ope.OrderId] = new OrderReadModel { Id = ope.OrderId };
        }
    }

    public async ValueTask FlushAsync()
    {
        if (_eventBuffer.Count == 0)
            return;

        // Save to store
        var json = JsonSerializer.Serialize(_models);
        await _store.SaveAsync("orders-projection", json);

        // Update position
        _position = _eventBuffer.Last().Position;

        // Clear buffer
        _eventBuffer.Clear();
    }
}
```

**Benefits:**
- 100x faster writes (batch updates)
- Lower database load
- Trade-off: losing durability until flush

## Pattern 6: Projection Composition

Combine multiple projections:

```csharp
/// <summary>
/// High-level projection that combines results from others.
/// </summary>
public class DashboardProjection
{
    private readonly OrderCompositeProjection _orderProjection;
    private readonly InventoryProjection _inventoryProjection;

    public class DashboardData
    {
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public int AvailableInventory { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    public DashboardData GetDashboard(CustomerId customerId)
    {
        return new DashboardData
        {
            TotalOrders = _orderProjection.GetOrderCount(customerId),
            TotalRevenue = _orderProjection.GetTotalRevenue(customerId),
            AvailableInventory = _inventoryProjection.GetInventory(productId)?.Available ?? 0,
            LastUpdate = DateTime.UtcNow
        };
    }
}
```

## Pattern 7: Event-Driven Side Effects

Trigger external actions from projections:

```csharp
/// <summary>
/// Projection that triggers side effects.
/// Example: Send email when order ships.
/// </summary>
public class OrderShippingNotificationProjection : Projection
{
    private readonly IEmailService _emailService;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        if (envelope.Event is OrderShippedEvent e)
        {
            // Trigger side effect: send email
            await _emailService.SendAsync(
                to: e.CustomerEmail,
                subject: $"Your order {e.OrderId} has shipped",
                body: $"Tracking number: {e.TrackingNumber}"
            );

            return true;
        }

        return false;
    }
}
```

**Caution:** Side effects must be idempotent (safe to run multiple times).

## Pattern 8: Time-Windowed Projections

Track events within time windows:

```csharp
/// <summary>
/// Projection that tracks orders in current week.
/// </summary>
public class WeeklyOrdersProjection : Projection
{
    private readonly Dictionary<DateTime, List<OrderReadModel>> _ordersByWeek = new();

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        if (envelope.Event is not OrderPlacedEvent e)
            return false;

        // Group by week
        var week = GetWeekStart(DateTime.UtcNow);

        if (!_ordersByWeek.ContainsKey(week))
            _ordersByWeek[week] = new();

        _ordersByWeek[week].Add(new OrderReadModel
        {
            Id = e.OrderId,
            Total = e.Total
        });

        return true;
    }

    private DateTime GetWeekStart(DateTime date)
    {
        var daysToSubtract = (int)date.DayOfWeek;
        return date.AddDays(-daysToSubtract).Date;
    }

    public List<OrderReadModel> GetCurrentWeekOrders()
    {
        var week = GetWeekStart(DateTime.UtcNow);
        return _ordersByWeek.GetValueOrDefault(week, new());
    }

    public decimal GetCurrentWeekRevenue()
        => GetCurrentWeekOrders().Sum(o => o.Total);
}
```

## Pattern 9: Projection State Snapshots

Save projection state periodically:

```csharp
public class PersistentProjection : Projection
{
    private readonly IProjectionStore _store;
    private Dictionary<OrderId, OrderReadModel> _models = new();
    private int _eventCount = 0;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        // Apply to model
        Apply(envelope.Event);
        _eventCount++;

        // Persist every 1000 events
        if (_eventCount % 1000 == 0)
        {
            var json = JsonSerializer.Serialize(_models);
            await _store.SaveAsync("orders", json);
        }

        return true;
    }

    public async ValueTask LoadAsync()
    {
        var json = await _store.LoadAsync("orders");
        if (json != null)
        {
            _models = JsonSerializer.Deserialize<Dictionary<OrderId, OrderReadModel>>(json);
        }
    }

    private void Apply(object @event)
    {
        // ... apply logic
    }
}
```

## Pattern 10: Error Handling in Projections

Handle errors gracefully:

```csharp
public class ResilientProjection : Projection
{
    private readonly ILogger _logger;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        try
        {
            return await ApplyInternalAsync(envelope);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Log but continue
            _logger.LogWarning($"Recoverable error applying event: {ex}");
            return false;  // Skip this event
        }
        catch (Exception ex)
        {
            // Non-recoverable error
            _logger.LogError($"Fatal error in projection: {ex}");
            throw;  // Stop projection
        }
    }

    private async ValueTask<bool> ApplyInternalAsync(EventEnvelope envelope)
    {
        // ... projection logic
        return true;
    }

    private bool IsRecoverable(Exception ex)
    {
        // Examples of recoverable errors
        return ex is TimeoutException || ex is OperationCanceledException;
    }
}
```

## Comparison: Patterns for Different Scenarios

| Pattern | Use Case | Complexity | Performance |
|---------|----------|-----------|-------------|
| Filtered | Only care about subset of events | Low | High (small model) |
| Composite | Multiple read models needed | Medium | High (single scan) |
| Stateful | Complex state transitions | High | Medium |
| Denormalized | Flat queries needed | Medium | High (pre-joined) |
| Batched | High throughput writes | Medium | Very High |
| Composed | Combining multiple projections | Medium | Low (multiple reads) |
| Side Effects | Trigger external actions | High | Medium |
| Time-Windowed | Time-based analysis | Medium | Medium |
| Persistent | Resume from checkpoint | High | Medium |
| Resilient | Fault tolerance needed | High | Medium |

## Testing Projections

```csharp
[TestClass]
public class ProjectionTests
{
    [TestMethod]
    public async Task ApplyAsync_BuildsReadModel()
    {
        var projection = new OrderCompositeProjection();

        var envelope = new EventEnvelope(
            new StreamId("order-123"),
            new StreamPosition(1),
            new OrderPlacedEvent(new OrderId(Guid.NewGuid()), customerId, 1000m),
            DateTimeOffset.UtcNow,
            new EventMetadata()
        );

        var applied = await projection.ApplyAsync(envelope);

        Assert.IsTrue(applied);
        Assert.AreEqual(1, projection.GetOrderCount(customerId));
        Assert.AreEqual(1000m, projection.GetTotalRevenue(customerId));
    }
}
```

## Summary

Advanced projection patterns enable:

1. **Filtered projections** — Reduce read model size
2. **Composite projections** — Multiple models from one stream
3. **Stateful projections** — Complex state machines
4. **Denormalized projections** — Pre-joined queries
5. **Batched projections** — High throughput
6. **Composed projections** — Combine multiple projections
7. **Side effects** — Trigger external actions
8. **Time-windowed** — Temporal analysis
9. **Persistent** — Survive restarts
10. **Resilient** — Handle errors gracefully

Choose patterns based on your query patterns and performance requirements.

## Next Steps

- **[Custom Event Stores](./custom-event-store.md)** — Custom storage backends
- **[Plugin Architecture](./plugin-architecture.md)** — Building extensible systems
- **[Core Concepts: Projections](../core-concepts/projections.md)** — Projection fundamentals
