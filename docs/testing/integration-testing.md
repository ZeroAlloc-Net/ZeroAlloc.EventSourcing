# Integration Testing

## Overview

Integration tests verify the complete event sourcing workflow: creating aggregates, executing commands, saving to event store, loading from storage, and verifying projections. This guide covers end-to-end testing strategies using real or containerized databases.

## Key Principles

1. **Full Workflow**: Test complete flow from command to storage to reload
2. **Real Storage**: Use actual event stores (SQL Server, PostgreSQL)
3. **Isolation**: Each test runs independently with fresh data
4. **Determinism**: Tests produce consistent results across runs
5. **Performance**: Verify acceptable performance under load

## Test Infrastructure Setup

### Test Domain and Serializer

The fixtures below store a small `ProductAggregate`. The event store also needs an
`IEventSerializer`; any implementation works. Tests can use a reflection-based JSON one,
while the application uses `ZeroAllocEventSerializer` (registered by `AddEventSourcing()`):

```csharp
using System.Text.Json;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;

public readonly record struct ProductId(Guid Value);

public record ProductCreatedEvent(string Name);
public record ProductDiscontinuedEvent();

public partial struct ProductState : IAggregateState<ProductState>
{
    public static ProductState Initial => default;
    public bool IsCreated { get; private set; }
    public bool IsDiscontinued { get; private set; }
    public string? Name { get; private set; }

    internal ProductState Apply(ProductCreatedEvent e) => this with { IsCreated = true, Name = e.Name };
    internal ProductState Apply(ProductDiscontinuedEvent _) => this with { IsDiscontinued = true };
}

// Partial, with no hand-written ApplyEvent: the source generator emits ApplyEvent
// and ProductAggregateEventTypeRegistry
public sealed partial class ProductAggregate : Aggregate<ProductId, ProductState>
{
    public void Create(string name)
    {
        if (State.IsCreated)
            throw new InvalidOperationException("Product already created");
        Raise(new ProductCreatedEvent(name));
    }

    public void Discontinue()
    {
        if (!State.IsCreated || State.IsDiscontinued)
            throw new InvalidOperationException("Only an active product can be discontinued");
        Raise(new ProductDiscontinuedEvent());
    }
}

// Test-only serializer: reflection-based System.Text.Json
public sealed class JsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => JsonSerializer.Deserialize(payload.Span, eventType)!;
}
```

### Using In-Memory Store (Fast)

```csharp
using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

public class IntegrationTestFixture : IAsyncLifetime
{
    private InMemoryEventStoreAdapter _adapter = null!;
    private EventStore _store = null!;
    public AggregateRepository<ProductAggregate, ProductId> ProductRepository = null!;
    
    public async Task InitializeAsync()
    {
        _adapter = new InMemoryEventStoreAdapter();
        var registry = new ProductAggregateEventTypeRegistry();
        var serializer = new JsonEventSerializer();  // your IEventSerializer, defined above
        
        _store = new EventStore(_adapter, serializer, registry);
        ProductRepository = new AggregateRepository<ProductAggregate, ProductId>(
            _store,
            () => new ProductAggregate(),
            id => new StreamId($"product-{id.Value}"));
        
        await Task.CompletedTask;
    }
    
    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }
}

public class ProductIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    
    public ProductIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task CompleteWorkflow_CreateAndLoad_StateRestored()
    {
        // Arrange
        var productId = new ProductId(Guid.NewGuid());
        
        // Act 1: Create and save
        using var product = new ProductAggregate();
        product.Create("Test Product");
        
        var saveResult = await _fixture.ProductRepository.SaveAsync(product, productId);
        saveResult.IsSuccess.Should().BeTrue();
        
        // Act 2: Load fresh
        var loadResult = await _fixture.ProductRepository.LoadAsync(productId);
        
        // Assert
        loadResult.IsSuccess.Should().BeTrue();
        using var loaded = loadResult.Value;
        loaded.State.IsCreated.Should().BeTrue();
        loaded.State.Name.Should().Be("Test Product");
        loaded.Version.Value.Should().Be(1);
    }
    
    [Fact]
    public async Task CompleteWorkflow_MultipleOperations_AllEventsStored()
    {
        // Arrange
        var productId = new ProductId(Guid.NewGuid());
        
        // Act: Create, modify, discontinue
        using var product = new ProductAggregate();
        product.Create("Gadget");
        await _fixture.ProductRepository.SaveAsync(product, productId);
        
        product.Discontinue();
        var secondSave = await _fixture.ProductRepository.SaveAsync(product, productId);
        
        // Act: Load and verify all events applied
        using var loaded = (await _fixture.ProductRepository.LoadAsync(productId)).Value;
        
        // Assert
        secondSave.IsSuccess.Should().BeTrue();
        loaded.State.IsCreated.Should().BeTrue();
        loaded.State.IsDiscontinued.Should().BeTrue();
        loaded.Version.Value.Should().Be(2);
    }
}
```

### Using Testcontainers (SQL Server/PostgreSQL)

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Xunit;
using ZeroAlloc.EventSourcing.SqlServer; // or PostgreSQL

[CollectionDefinition("Database", DisableParallelization = true)]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    // This has no code, just used to group tests
}

public class DatabaseFixture : IAsyncLifetime
{
    private IContainer? _container;
    private string _connectionString = null!;
    
    public string ConnectionString => _connectionString;
    
    public async Task InitializeAsync()
    {
        // Start SQL Server container
        _container = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithEnvironment("SA_PASSWORD", "YourPassword123!")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithPortBinding(1433, 1433)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted(
                        "/opt/mssql-tools18/bin/sqlcmd", "-C", "-S", "localhost",
                        "-U", "sa", "-P", "YourPassword123!", "-Q", "SELECT 1")
            )
            .Build();
        
        await _container.StartAsync();
        
        _connectionString = "Server=localhost;User Id=sa;Password=YourPassword123!;TrustServerCertificate=true";
        
        // Run migrations
        await InitializeDatabaseAsync();
    }
    
    public async Task DisposeAsync()
    {
        if (_container != null)
            await _container.StopAsync();
    }
    
    private async Task InitializeDatabaseAsync()
    {
        // Create the database
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = 'EventStore')
                BEGIN
                    CREATE DATABASE EventStore;
                END
            ";
            await command.ExecuteNonQueryAsync();
        }

        _connectionString += ";Database=EventStore";

        // Create the adapter's own tables, if they do not exist yet
        await new SqlServerEventStoreAdapter(_connectionString).EnsureSchemaAsync();
    }
}

[Collection("Database")]
public class SqlServerIntegrationTests
{
    private readonly DatabaseFixture _databaseFixture;
    
    public SqlServerIntegrationTests(DatabaseFixture databaseFixture)
    {
        _databaseFixture = databaseFixture;
    }
    
    [Fact]
    public async Task CompleteWorkflow_WithRealDatabase_PersistsAndLoads()
    {
        // Arrange: Create repository with real database
        var adapter = new SqlServerEventStoreAdapter(_databaseFixture.ConnectionString);
        var registry = new ProductAggregateEventTypeRegistry();
        var serializer = new JsonEventSerializer();  // your IEventSerializer, defined above
        var store = new EventStore(adapter, serializer, registry);
        
        var repository = new AggregateRepository<ProductAggregate, ProductId>(
            store,
            () => new ProductAggregate(),
            id => new StreamId($"product-{id.Value}"));
        
        var productId = new ProductId(Guid.NewGuid());
        
        // Act: Create and save
        using var product = new ProductAggregate();
        product.Create($"Product-{Guid.NewGuid()}");
        
        var saveResult = await repository.SaveAsync(product, productId);
        
        // Assert: Successfully persisted
        saveResult.IsSuccess.Should().BeTrue();
        
        // Act: Load fresh instance
        var loadResult = await repository.LoadAsync(productId);
        
        // Assert: Loaded from database
        loadResult.IsSuccess.Should().BeTrue();
        using var loaded = loadResult.Value;
        loaded.State.Name.Should().StartWith("Product-");
    }
}
```

## Complete Order Integration Test Suite

### Domain Models

```csharp
public readonly record struct OrderId(Guid Value);

public record OrderPlacedEvent(string OrderId, decimal Total);
public record OrderConfirmedEvent();
public record OrderShippedEvent(string TrackingNumber);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? TrackingNumber { get; private set; }
    
    internal OrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };
    
    internal OrderState Apply(OrderConfirmedEvent _) =>
        this with { IsConfirmed = true };
    
    internal OrderState Apply(OrderShippedEvent e) =>
        this with { IsShipped = true, TrackingNumber = e.TrackingNumber };
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void PlaceOrder(string orderId, decimal total)
    {
        if (total <= 0)
            throw new ArgumentException("Total must be positive");

        Raise(new OrderPlacedEvent(orderId, total));
    }

    public void ConfirmOrder()
    {
        if (!State.IsPlaced || State.IsConfirmed)
            throw new InvalidOperationException("Only a placed, unconfirmed order can be confirmed");

        Raise(new OrderConfirmedEvent());
    }

    public void ShipOrder(string tracking) =>
        Raise(new OrderShippedEvent(tracking));

    public void SetId(OrderId id) => Id = id;
}
```

`Order` is `partial` and does not write `ApplyEvent` by hand, so the source generator emits both
the `ApplyEvent` switch and the `OrderEventTypeRegistry` the event store needs. A hand-written
`ApplyEvent` turns both off.

The repository builder is shared by all test classes on this page:

```csharp
internal static class TestRepositories
{
    public static AggregateRepository<Order, OrderId> BuildOrderRepository()
        => BuildOrderRepository(out _);

    public static AggregateRepository<Order, OrderId> BuildOrderRepository(out IEventStore store)
    {
        var adapter = new InMemoryEventStoreAdapter();
        var registry = new OrderEventTypeRegistry();       // source-generated for Order
        var serializer = new JsonEventSerializer();       // your IEventSerializer, defined above
        store = new EventStore(adapter, serializer, registry);

        return new AggregateRepository<Order, OrderId>(
            store,
            () => new Order(),
            id => new StreamId($"order-{id.Value}"));
    }
}

// In each test file:
// using static TestRepositories;
```

### Test Class

```csharp
public class OrderIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    
    public OrderIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task CompleteOrderFlow_PlaceConfirmShip_AllEventsStored()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        // Act 1: Place order
        var order = new Order();
        order.SetId(orderId);
        order.PlaceOrder("ORD-001", 99.99m);
        
        var result1 = await repository.SaveAsync(order, orderId);
        result1.IsSuccess.Should().BeTrue();
        
        // Act 2: Confirm order
        var loaded1 = (await repository.LoadAsync(orderId)).Value;
        loaded1.ConfirmOrder();
        
        var result2 = await repository.SaveAsync(loaded1, orderId);
        result2.IsSuccess.Should().BeTrue();
        
        // Act 3: Ship order
        var loaded2 = (await repository.LoadAsync(orderId)).Value;
        loaded2.ShipOrder("TRACK-123456");
        
        var result3 = await repository.SaveAsync(loaded2, orderId);
        result3.IsSuccess.Should().BeTrue();
        
        // Act 4: Load and verify final state
        var final = (await repository.LoadAsync(orderId)).Value;
        
        // Assert: All events applied correctly
        final.State.IsPlaced.Should().BeTrue();
        final.State.IsConfirmed.Should().BeTrue();
        final.State.IsShipped.Should().BeTrue();
        final.State.Total.Should().Be(99.99m);
        final.State.TrackingNumber.Should().Be("TRACK-123456");
        final.Version.Value.Should().Be(3);
    }
    
    [Fact]
    public async Task ConcurrentOrders_EachMaintainedSeparately()
    {
        // Arrange
        var orderId1 = new OrderId(Guid.NewGuid());
        var orderId2 = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        // Act: Create two orders
        var order1 = new Order();
        order1.SetId(orderId1);
        order1.PlaceOrder("ORD-001", 100m);
        
        var order2 = new Order();
        order2.SetId(orderId2);
        order2.PlaceOrder("ORD-002", 50m);
        
        await repository.SaveAsync(order1, orderId1);
        await repository.SaveAsync(order2, orderId2);
        
        // Act: Modify first order only
        var loaded1 = (await repository.LoadAsync(orderId1)).Value;
        loaded1.ConfirmOrder();
        await repository.SaveAsync(loaded1, orderId1);
        
        // Act: Load both and verify isolation
        var final1 = (await repository.LoadAsync(orderId1)).Value;
        var final2 = (await repository.LoadAsync(orderId2)).Value;
        
        // Assert: Changes to order1 don't affect order2
        final1.State.IsConfirmed.Should().BeTrue();
        final2.State.IsConfirmed.Should().BeFalse();
        final1.Version.Value.Should().Be(2);
        final2.Version.Value.Should().Be(1);
    }
    
    [Fact]
    public async Task OptimisticLocking_ConflictDetected_WhenVersionMismatches()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        var order = new Order();
        order.SetId(orderId);
        order.PlaceOrder("ORD-001", 100m);
        await repository.SaveAsync(order, orderId);
        
        // Act: Create two instances from same saved order
        var loaded1 = (await repository.LoadAsync(orderId)).Value;
        var loaded2 = (await repository.LoadAsync(orderId)).Value;
        
        // Modify first
        loaded1.ConfirmOrder();
        var result1 = await repository.SaveAsync(loaded1, orderId);
        result1.IsSuccess.Should().BeTrue();
        
        // Try to modify second (stale)
        loaded2.ConfirmOrder();
        var result2 = await repository.SaveAsync(loaded2, orderId);
        
        // Assert: Conflict detected
        result2.IsFailure.Should().BeTrue();
        result2.Error.Code.Should().Be("CONFLICT");
    }
    
    [Fact]
    public async Task SaveMultipleTimes_VersionIncrementsCorrectly()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        var order = new Order();
        order.SetId(orderId);
        
        // Act: Save multiple times
        for (int i = 0; i < 5; i++)
        {
            order.PlaceOrder($"ORD-{i}", (i + 1) * 10m);
            var result = await repository.SaveAsync(order, orderId);
            result.IsSuccess.Should().BeTrue();
            
            // Reload for next iteration
            order = (await repository.LoadAsync(orderId)).Value;
        }
        
        // Act: Final load
        var final = (await repository.LoadAsync(orderId)).Value;
        
        // Assert: Version incremented with each event
        final.Version.Value.Should().Be(5);
    }
    
    [Fact]
    public async Task LoadNonExistentOrder_ReturnsFreshAggregate()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();

        // Act
        var result = await repository.LoadAsync(orderId);

        // Assert: a stream with no events loads as a new aggregate, not as an error
        result.IsSuccess.Should().BeTrue();
        using var order = result.Value;
        order.Version.Should().Be(StreamPosition.Start);
        order.State.IsPlaced.Should().BeFalse();
    }
}
```

## Snapshot Integration Testing

```csharp
public class SnapshotIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    
    public SnapshotIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task SnapshotLoad_RestoresSnapshotAndReplaysTheTail()
    {
        // Arrange: a repository that writes a snapshot on every save
        var orderId = new OrderId(Guid.NewGuid());
        var innerRepository = BuildOrderRepository(out var eventStore);
        var snapshotStore = new InMemorySnapshotStore<OrderState>();
        var repository = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
            innerRepository: innerRepository,
            snapshotStore: snapshotStore,
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (o, state, pos) => o.RestoreState(state, pos),
            eventStore: eventStore,
            streamIdFactory: id => new StreamId($"order-{id.Value}"),
            aggregateFactory: () => new Order(),
            snapshotPolicy: SnapshotPolicy.Always,
            extractState: o => o.State);

        // Act 1: Save the placed order; the decorator snapshots it at position 1
        using (var order = new Order())
        {
            order.PlaceOrder("ORD-001", 100m);
            (await repository.SaveAsync(order, orderId)).IsSuccess.Should().BeTrue();
        }

        var snapshot = await snapshotStore.ReadAsync(new StreamId($"order-{orderId.Value}"));
        snapshot.Should().NotBeNull();
        snapshot!.Value.Position.Should().Be(new StreamPosition(1));
        snapshot.Value.State.IsPlaced.Should().BeTrue();

        // Act 2: Append more events without going through the decorator, so no new snapshot
        using (var loaded = (await innerRepository.LoadAsync(orderId)).Value)
        {
            loaded.ConfirmOrder();
            loaded.ShipOrder("TRACK-123");
            (await innerRepository.SaveAsync(loaded, orderId)).IsSuccess.Should().BeTrue();
        }

        // Act 3: Load through the decorator: snapshot at 1, then events 2 and 3
        using var final = (await repository.LoadAsync(orderId)).Value;

        // Assert: same result as a full replay
        final.State.IsShipped.Should().BeTrue();
        final.State.Total.Should().Be(100m);
        final.Version.Value.Should().Be(3);
        final.OriginalVersion.Value.Should().Be(3);
    }
}
```

## Projection Integration Testing

```csharp
public class ProjectionIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    
    public ProjectionIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task Projection_ConsumesOrderEvents_BuildsReadModel()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository(out var store);
        var projection = new OrderListProjection();

        // Act: Create order through aggregate
        using var order = new Order();
        order.PlaceOrder("ORD-001", 99.99m);
        order.ConfirmOrder();

        (await repository.SaveAsync(order, orderId)).IsSuccess.Should().BeTrue();

        // Act: Deliver the stored events to the projection
        await foreach (var envelope in store.ReadAsync(new StreamId($"order-{orderId.Value}")))
            await projection.HandleAsync(envelope);

        // Assert: Projection has correct state
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(99.99m);
        projection.Current.IsConfirmed.Should().BeTrue();
    }
}

// The read model and its projection
public record OrderListItem(string OrderId, decimal Amount, bool IsConfirmed);

public class OrderListProjection : Projection<OrderListItem>
{
    public OrderListProjection() => Current = new OrderListItem("", 0m, false);

    protected override OrderListItem Apply(OrderListItem current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current with { OrderId = e.OrderId, Amount = e.Total },
        OrderConfirmedEvent => current with { IsConfirmed = true },
        _ => current
    };
}
```

## Performance Integration Tests

```csharp
public class PerformanceIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    
    public PerformanceIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task SaveOrder_CompletesWithin_100ms()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        var order = new Order();
        order.SetId(orderId);
        order.PlaceOrder("ORD-001", 100m);
        
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await repository.SaveAsync(order, orderId);
        stopwatch.Stop();
        
        // Assert
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }
    
    [Fact]
    public async Task LoadOrder_CompletesWithin_50ms()
    {
        // Arrange: Pre-populate order
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        var order = new Order();
        order.SetId(orderId);
        order.PlaceOrder("ORD-001", 100m);
        await repository.SaveAsync(order, orderId);
        
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await repository.LoadAsync(orderId);
        stopwatch.Stop();
        
        // Assert
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(50);
    }
    
    [Fact]
    public async Task BulkLoadOrders_100Orders_CompletesWithin_5seconds()
    {
        // Arrange
        var repository = BuildOrderRepository();
        var orderIds = new List<OrderId>();
        
        // Create 100 orders
        for (int i = 0; i < 100; i++)
        {
            var orderId = new OrderId(Guid.NewGuid());
            orderIds.Add(orderId);
            
            var order = new Order();
            order.SetId(orderId);
            order.PlaceOrder($"ORD-{i:D4}", (i + 1) * 10m);
            await repository.SaveAsync(order, orderId);
        }
        
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        foreach (var orderId in orderIds)
            await repository.LoadAsync(orderId);
        
        stopwatch.Stop();
        
        // Assert
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
    }
}
```

## Error Scenario Testing

```csharp
public class ErrorScenarioTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    
    public ErrorScenarioTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public void SaveWithInvalidCommand_RaisesException()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        var order = new Order();
        order.SetId(orderId);
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            order.PlaceOrder("ORD-001", -100m) // Invalid total
        );
    }
    
    [Fact]
    public void ConfirmUnplacedOrder_RaisesException()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();
        
        var order = new Order();
        order.SetId(orderId);
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            order.ConfirmOrder() // No order placed
        );
    }
    
    [Fact]
    public async Task LoadMissingStream_ReturnsFreshAggregate()
    {
        // Arrange
        var orderId = new OrderId(Guid.NewGuid());
        var repository = BuildOrderRepository();

        // Act: Try to load non-existent order
        var result = await repository.LoadAsync(orderId);

        // Assert: LoadAsync does not fail for a missing stream; check the version instead
        result.IsSuccess.Should().BeTrue();
        using var order = result.Value;
        order.Version.Should().Be(StreamPosition.Start);
    }
}
```

## Best Practices

1. **Fixtures for Setup**: Use IAsyncLifetime for test infrastructure
2. **Real Storage**: Test with actual databases when possible
3. **Isolation**: Each test gets fresh data
4. **Collection Fixtures**: Share expensive resources (containers)
5. **Deterministic**: Tests produce consistent results
6. **Fast Feedback**: Use in-memory store for speed, containers for reality
7. **End-to-End**: Test complete workflows, not just single operations
8. **Error Paths**: Test both success and failure scenarios
9. **Performance Assertions**: Verify acceptable response times
10. **Cleanup**: Ensure test data is properly cleaned up

## Summary

Integration testing in ZeroAlloc.EventSourcing verifies:
- Complete workflows from command through save to reload
- Event persistence and recovery from storage
- Optimistic locking and concurrency handling
- Version tracking and increments
- Snapshot optimization and storage
- Projection consumption and read model accuracy
- Performance expectations and benchmarks
- Error handling and edge cases
- Multi-order isolation and independence

With these patterns, you can build comprehensive integration tests that verify your entire event sourcing system works correctly end-to-end.
