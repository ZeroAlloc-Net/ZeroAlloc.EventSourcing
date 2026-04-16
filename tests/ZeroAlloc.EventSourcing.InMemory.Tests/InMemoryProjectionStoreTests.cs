using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryProjectionStore"/>. Verifies that the in-memory store
/// correctly saves, loads, and clears projection state.
/// Also inherits all contract tests from <see cref="ProjectionStoreContractTests"/>.
/// </summary>
public class InMemoryProjectionStoreTests : ProjectionStoreContractTests
{
    protected override IProjectionStore CreateStore() => new InMemoryProjectionStore();

    [Fact]
    public async Task SaveThenLoad_ReturnsStoredState()
    {
        // Arrange
        var store = new InMemoryProjectionStore();
        var key = "OrderProjection";
        var state = """{"orderId":"ORD-001","amount":99.99}""";

        // Act
        await store.SaveAsync(key, state);
        var loaded = await store.LoadAsync(key);

        // Assert
        loaded.Should().Be(state);
    }

    [Fact]
    public async Task LoadNoState_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryProjectionStore();

        // Act
        var result = await store.LoadAsync("NonExistentKey");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveMultiple_LastWins()
    {
        // Arrange
        var store = new InMemoryProjectionStore();
        var key = "InventoryProjection";
        var state1 = """{"sku":"ITEM-001","quantity":100}""";
        var state2 = """{"sku":"ITEM-001","quantity":95}""";

        // Act
        await store.SaveAsync(key, state1);
        await store.SaveAsync(key, state2);
        var loaded = await store.LoadAsync(key);

        // Assert
        loaded.Should().Be(state2);
    }

    [Fact]
    public async Task Clear_RemovesStoredState()
    {
        // Arrange
        var store = new InMemoryProjectionStore();
        var key = "PaymentProjection";
        var state = """{"paymentId":"PAY-001","status":"Completed"}""";
        await store.SaveAsync(key, state);

        // Act
        await store.ClearAsync(key);
        var loaded = await store.LoadAsync(key);

        // Assert
        loaded.Should().BeNull();
    }
}
