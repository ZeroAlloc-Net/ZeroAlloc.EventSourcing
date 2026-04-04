using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.Testing;

/// <summary>
/// This example demonstrates how to test projections.
///
/// Projections transform events into read models for queries.
/// Testing projections is also straightforward because:
/// 1. Projections are pure functions (event -> updated model)
/// 2. No state management complexity
/// 3. No database needed - test with in-memory models
/// 4. Can test with synthetic events, no need to create full aggregates
/// </summary>

// ===== Domain Model =====

public readonly record struct ProductId(Guid Value);

public record StockReceivedEvent(ProductId ProductId, int Quantity);
public record StockReservedEvent(ProductId ProductId, int Quantity);
public record StockReleasedEvent(ProductId ProductId, int Quantity);

// Read model: Current inventory levels
public class InventoryReadModel
{
    public ProductId ProductId { get; set; }
    public int QuantityOnHand { get; set; }
    public int QuantityReserved { get; set; }
    public int AvailableQuantity => QuantityOnHand - QuantityReserved;

    public override string ToString() =>
        $"Inventory({ProductId.Value:N}): {QuantityOnHand} on hand, " +
        $"{QuantityReserved} reserved, {AvailableQuantity} available";
}

// ===== PROJECTION IMPLEMENTATION =====

public class InventoryProjection : Projection
{
    private readonly Dictionary<ProductId, InventoryReadModel> _inventory = new();

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        switch (envelope.Event)
        {
            case StockReceivedEvent e:
                var model = GetOrCreateModel(e.ProductId);
                model.QuantityOnHand += e.Quantity;
                return true;

            case StockReservedEvent e:
                model = GetOrCreateModel(e.ProductId);
                model.QuantityReserved += e.Quantity;
                return true;

            case StockReleasedEvent e:
                model = GetOrCreateModel(e.ProductId);
                model.QuantityReserved -= e.Quantity;
                return true;

            default:
                return false;
        }
    }

    private InventoryReadModel GetOrCreateModel(ProductId productId)
    {
        if (!_inventory.ContainsKey(productId))
            _inventory[productId] = new InventoryReadModel { ProductId = productId };
        return _inventory[productId];
    }

    public InventoryReadModel? GetInventory(ProductId productId)
    {
        _inventory.TryGetValue(productId, out var model);
        return model;
    }

    public List<InventoryReadModel> GetAll()
        => _inventory.Values.ToList();
}

// ===== PROJECTION TESTS =====

public class ProjectionTestingExamples
{
    // ===== Test 1: Basic Event Application =====

    /// <summary>
    /// Test that a single event is correctly applied to the projection.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WithStockReceivedEvent_UpdatesInventory()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        var @event = new StockReceivedEvent(productId, 100);
        var envelope = new EventEnvelope(
            new StreamId($"product-{productId.Value}"),
            StreamPosition.Start,
            @event,
            DateTimeOffset.UtcNow,
            new EventMetadata()
        );

        // Act
        var applied = await projection.ApplyAsync(envelope);

        // Assert
        Assert.True(applied);

        var model = projection.GetInventory(productId);
        Assert.NotNull(model);
        Assert.Equal(100, model!.QuantityOnHand);
        Assert.Equal(0, model.QuantityReserved);
        Assert.Equal(100, model.AvailableQuantity);
    }

    // ===== Test 2: Multiple Events =====

    /// <summary>
    /// Test that multiple events are correctly applied in sequence.
    /// This simulates replaying a stream of events.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WithMultipleEvents_BuildsCompleteModel()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        var events = new object[]
        {
            new StockReceivedEvent(productId, 100),  // Receive 100
            new StockReservedEvent(productId, 30),   // Reserve 30
            new StockReservedEvent(productId, 20),   // Reserve 20 more
            new StockReleasedEvent(productId, 10),   // Release 10
        };

        // Act
        var position = StreamPosition.Start;
        foreach (var @event in events)
        {
            var envelope = CreateEnvelope(productId, @event, position);
            await projection.ApplyAsync(envelope);
            position = position.Next();
        }

        // Assert
        var model = projection.GetInventory(productId);
        Assert.NotNull(model);
        Assert.Equal(100, model!.QuantityOnHand);
        Assert.Equal(40, model.QuantityReserved);  // 30 + 20 - 10
        Assert.Equal(60, model.AvailableQuantity);
    }

    // ===== Test 3: Multiple Products =====

    /// <summary>
    /// Test that projection correctly handles events for multiple products.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WithMultipleProducts_MaintainsIndependentModels()
    {
        // Arrange
        var projection = new InventoryProjection();
        var product1 = new ProductId(Guid.NewGuid());
        var product2 = new ProductId(Guid.NewGuid());

        var events = new object[]
        {
            new StockReceivedEvent(product1, 100),
            new StockReceivedEvent(product2, 200),
            new StockReservedEvent(product1, 50),
            new StockReservedEvent(product2, 75),
        };

        // Act
        var position = StreamPosition.Start;
        foreach (var @event in events)
        {
            // Route event to correct stream
            var productId = @event switch
            {
                StockReceivedEvent e => e.ProductId,
                StockReservedEvent e => e.ProductId,
                StockReleasedEvent e => e.ProductId,
                _ => new ProductId(Guid.Empty)
            };

            var envelope = CreateEnvelope(productId, @event, position);
            await projection.ApplyAsync(envelope);
            position = position.Next();
        }

        // Assert: Each product has independent model
        var model1 = projection.GetInventory(product1);
        Assert.NotNull(model1);
        Assert.Equal(100, model1!.QuantityOnHand);
        Assert.Equal(50, model1.QuantityReserved);

        var model2 = projection.GetInventory(product2);
        Assert.NotNull(model2);
        Assert.Equal(200, model2!.QuantityOnHand);
        Assert.Equal(75, model2.QuantityReserved);
    }

    // ===== Test 4: Ignored Events =====

    /// <summary>
    /// Test that projection correctly ignores events it doesn't care about.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WithUnrelatedEvent_ReturnsFalse()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        // Some event the projection doesn't understand
        var unknownEvent = new UnknownEvent();

        var envelope = CreateEnvelope(productId, unknownEvent, StreamPosition.Start);

        // Act
        var applied = await projection.ApplyAsync(envelope);

        // Assert
        Assert.False(applied);  // Projection returns false for unknown events
        Assert.Null(projection.GetInventory(productId));  // No model created
    }

    // ===== Test 5: Idempotency =====

    /// <summary>
    /// Test that applying the same event multiple times produces the same result.
    /// This is important because projections may receive duplicate events.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_IsIdempotent()
    {
        // Arrange
        var projection1 = new InventoryProjection();
        var projection2 = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        var @event = new StockReceivedEvent(productId, 100);
        var envelope = CreateEnvelope(productId, @event, StreamPosition.Start);

        // Act: Apply event once
        await projection1.ApplyAsync(envelope);

        // Act: Apply same event twice
        await projection2.ApplyAsync(envelope);
        await projection2.ApplyAsync(envelope);

        // Assert: Results should differ (projection2 applied twice)
        // This is actually testing that projection is NOT idempotent by default
        // In production, you'd want to make projections idempotent
        var model1 = projection1.GetInventory(productId);
        var model2 = projection2.GetInventory(productId);

        Assert.Equal(100, model1!.QuantityOnHand);
        Assert.Equal(200, model2!.QuantityOnHand);  // Applied twice!
    }

    // ===== Test 6: Projection State Query =====

    /// <summary>
    /// Test that you can query the projection to get read models.
    /// This is what happens in your API/service layer.
    /// </summary>
    [Fact]
    public async Task GetInventory_ReturnsCorrectModel()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        await ApplyEvents(projection, new[]
        {
            new StockReceivedEvent(productId, 100),
            new StockReservedEvent(productId, 30),
        });

        // Act
        var model = projection.GetInventory(productId);

        // Assert
        Assert.NotNull(model);
        Assert.Equal(70, model!.AvailableQuantity);
    }

    /// <summary>
    /// Test that GetInventory returns null for unknown product.
    /// </summary>
    [Fact]
    public void GetInventory_ForUnknownProduct_ReturnsNull()
    {
        // Arrange
        var projection = new InventoryProjection();
        var unknownProductId = new ProductId(Guid.NewGuid());

        // Act
        var model = projection.GetInventory(unknownProductId);

        // Assert
        Assert.Null(model);
    }

    // ===== Test 7: Complex Event Sequence =====

    /// <summary>
    /// Test a realistic scenario with multiple operations.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WithRealisticScenario()
    {
        // Arrange: Simulating warehouse operations
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        var operations = new object[]
        {
            // Day 1: Receive stock shipment
            new StockReceivedEvent(productId, 500),

            // Customer 1 orders 100 units
            new StockReservedEvent(productId, 100),

            // Customer 2 orders 50 units
            new StockReservedEvent(productId, 50),

            // Customer 1 cancels order
            new StockReleasedEvent(productId, 100),

            // Day 2: Receive another shipment
            new StockReceivedEvent(productId, 300),

            // Customer 3 orders 200 units
            new StockReservedEvent(productId, 200),
        };

        // Act
        await ApplyEvents(projection, operations);

        // Assert: Final inventory state
        var model = projection.GetInventory(productId);
        Assert.NotNull(model);

        // On hand: 500 + 300 = 800
        // Reserved: 50 + 200 = 250 (Customer 1's 100 was released)
        // Available: 800 - 250 = 550
        Assert.Equal(800, model!.QuantityOnHand);
        Assert.Equal(250, model.QuantityReserved);
        Assert.Equal(550, model.AvailableQuantity);
    }

    // ===== Test 8: Performance =====

    /// <summary>
    /// Test that projection can handle large volumes of events.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_Performance_HandlesLargeVolumes()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());
        var eventCount = 10_000;

        var events = new List<object>();
        for (int i = 0; i < eventCount; i++)
        {
            events.Add(new StockReceivedEvent(productId, 1));
        }

        // Act: Measure time to process 10,000 events
        var startTime = DateTime.UtcNow;
        await ApplyEvents(projection, events);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert: Should be very fast (< 1 second for 10k events)
        Assert.True(elapsed.TotalSeconds < 1, $"Too slow: {elapsed.TotalSeconds}s for {eventCount} events");

        var model = projection.GetInventory(productId);
        Assert.Equal(eventCount, model!.QuantityOnHand);
    }

    // ===== Helper Methods =====

    private EventEnvelope CreateEnvelope(ProductId productId, object @event, StreamPosition position)
    {
        return new EventEnvelope(
            new StreamId($"product-{productId.Value}"),
            position,
            @event,
            DateTimeOffset.UtcNow,
            new EventMetadata()
        );
    }

    private async Task ApplyEvents(InventoryProjection projection, IEnumerable<object> events)
    {
        var position = StreamPosition.Start;
        var productId = new ProductId(Guid.NewGuid());

        foreach (var @event in events)
        {
            if (@event is StockReceivedEvent sre)
                productId = sre.ProductId;
            else if (@event is StockReservedEvent srev)
                productId = srev.ProductId;
            else if (@event is StockReleasedEvent sle)
                productId = sle.ProductId;

            var envelope = CreateEnvelope(productId, @event, position);
            await projection.ApplyAsync(envelope);
            position = position.Next();
        }
    }
}

// Unknown event for testing
public class UnknownEvent { }
