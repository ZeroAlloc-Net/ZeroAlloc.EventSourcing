using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// 1. Apply is a pure function (current read model + event -> new read model)
/// 2. No state management complexity
/// 3. No database needed - test with in-memory models
/// 4. Can test with synthetic events, no need to create full aggregates
///
/// This file is compiled and its tests run by the test suite, so it only uses the public API.
/// </summary>

// ===== Domain Model =====

public readonly record struct ProductId(Guid Value);

public record StockReceivedEvent(ProductId ProductId, int Quantity);
public record StockReservedEvent(ProductId ProductId, int Quantity);
public record StockReleasedEvent(ProductId ProductId, int Quantity);

// Read model: Current inventory level of one product
public sealed record InventoryReadModel(ProductId ProductId, int QuantityOnHand, int QuantityReserved)
{
    public int AvailableQuantity => QuantityOnHand - QuantityReserved;

    public override string ToString() =>
        $"Inventory({ProductId.Value:N}): {QuantityOnHand} on hand, " +
        $"{QuantityReserved} reserved, {AvailableQuantity} available";
}

// ===== PROJECTION IMPLEMENTATION =====

/// <summary>
/// Projects stock events into one <see cref="InventoryReadModel"/> per product. The read model of
/// the projection is the immutable map of all of them.
/// </summary>
public sealed class InventoryProjection : Projection<ImmutableDictionary<ProductId, InventoryReadModel>>
{
    public InventoryProjection()
    {
        // Projection<T>.Current starts at default, which is null for a class: start empty instead.
        Current = ImmutableDictionary<ProductId, InventoryReadModel>.Empty;
    }

    protected override ImmutableDictionary<ProductId, InventoryReadModel> Apply(
        ImmutableDictionary<ProductId, InventoryReadModel> current,
        EventEnvelope envelope)
        => envelope.Event switch
        {
            StockReceivedEvent e => Update(current, e.ProductId, m => m with { QuantityOnHand = m.QuantityOnHand + e.Quantity }),
            StockReservedEvent e => Update(current, e.ProductId, m => m with { QuantityReserved = m.QuantityReserved + e.Quantity }),
            StockReleasedEvent e => Update(current, e.ProductId, m => m with { QuantityReserved = m.QuantityReserved - e.Quantity }),
            // Events the projection does not handle leave the read model unchanged
            _ => current
        };

    private static ImmutableDictionary<ProductId, InventoryReadModel> Update(
        ImmutableDictionary<ProductId, InventoryReadModel> current,
        ProductId productId,
        Func<InventoryReadModel, InventoryReadModel> change)
    {
        var model = current.GetValueOrDefault(productId) ?? new InventoryReadModel(productId, 0, 0);
        return current.SetItem(productId, change(model));
    }

    public InventoryReadModel? GetInventory(ProductId productId)
        => Current.GetValueOrDefault(productId);

    public List<InventoryReadModel> GetAll()
        => Current.Values.ToList();
}

// ===== PROJECTION TESTS =====

public class ProjectionTestingExamples
{
    // ===== Test 1: Basic Event Application =====

    /// <summary>
    /// Test that a single event is correctly applied to the projection.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithStockReceivedEvent_UpdatesInventory()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        var @event = new StockReceivedEvent(productId, 100);
        var envelope = new EventEnvelope(
            new StreamId($"product-{productId.Value}"),
            new StreamPosition(1),
            @event,
            EventMetadata.New(nameof(StockReceivedEvent)));

        // Act
        await projection.HandleAsync(envelope);

        // Assert
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
    public async Task HandleAsync_WithMultipleEvents_BuildsCompleteModel()
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
        await ApplyEvents(projection, events);

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
    public async Task HandleAsync_WithMultipleProducts_MaintainsIndependentModels()
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
        await ApplyEvents(projection, events);

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
    public async Task HandleAsync_WithUnrelatedEvent_LeavesReadModelUnchanged()
    {
        // Arrange
        var projection = new InventoryProjection();
        var before = projection.Current;

        // Act: Some event the projection doesn't understand
        await ApplyEvents(projection, new object[] { new UnknownEvent() });

        // Assert
        Assert.Same(before, projection.Current);  // Apply returned the read model it was given
        Assert.Empty(projection.GetAll());         // No model created
    }

    // ===== Test 5: Duplicate Delivery =====

    /// <summary>
    /// Test what happens when the same event is delivered twice.
    /// A projection may see duplicates, for example after a consumer restarts from its last
    /// checkpoint. This projection is not idempotent: it counts the stock twice. Store the
    /// last applied position with the read model and skip older events if that matters.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SameEventTwice_IsAppliedTwice()
    {
        // Arrange
        var projection1 = new InventoryProjection();
        var projection2 = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());

        var envelope = CreateEnvelope(new StockReceivedEvent(productId, 100), new StreamPosition(1));

        // Act: Apply event once
        await projection1.HandleAsync(envelope);

        // Act: Apply same event twice
        await projection2.HandleAsync(envelope);
        await projection2.HandleAsync(envelope);

        // Assert: projection2 counted the duplicate
        Assert.Equal(100, projection1.GetInventory(productId)!.QuantityOnHand);
        Assert.Equal(200, projection2.GetInventory(productId)!.QuantityOnHand);
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

        await ApplyEvents(projection, new object[]
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
    public async Task HandleAsync_WithRealisticScenario()
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

    // ===== Test 8: Volume =====

    /// <summary>
    /// Test that projection can handle large volumes of events. Measure speed with a benchmark
    /// rather than a timing assertion, which fails at random on a busy build machine.
    /// </summary>
    [Fact]
    public async Task HandleAsync_HandlesLargeVolumes()
    {
        // Arrange
        var projection = new InventoryProjection();
        var productId = new ProductId(Guid.NewGuid());
        const int eventCount = 10_000;

        var events = Enumerable.Range(0, eventCount)
            .Select(_ => (object)new StockReceivedEvent(productId, 1))
            .ToList();

        // Act
        await ApplyEvents(projection, events);

        // Assert
        Assert.Equal(eventCount, projection.GetInventory(productId)!.QuantityOnHand);
    }

    // ===== Helper Methods =====

    private static EventEnvelope CreateEnvelope(object @event, StreamPosition position)
        => new(
            new StreamId("inventory"),
            position,
            @event,
            EventMetadata.New(@event.GetType().Name));

    // Delivers the events in order, at positions 1, 2, 3... as a stream would
    private static async Task ApplyEvents(InventoryProjection projection, IEnumerable<object> events)
    {
        var position = StreamPosition.Start;
        foreach (var @event in events)
        {
            position = position.Next();
            await projection.HandleAsync(CreateEnvelope(@event, position));
        }
    }
}

// Unknown event for testing
public class UnknownEvent { }
