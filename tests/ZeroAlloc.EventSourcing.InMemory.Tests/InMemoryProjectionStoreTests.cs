using AwesomeAssertions;
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
        await store.SaveAsync(key, Utf8(state));
        var loaded = await store.LoadAsync(key);

        // Assert
        Text(loaded).Should().Be(state);
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
        await store.SaveAsync(key, Utf8(state1));
        await store.SaveAsync(key, Utf8(state2));
        var loaded = await store.LoadAsync(key);

        // Assert
        Text(loaded).Should().Be(state2);
    }

    [Fact]
    public async Task Clear_RemovesStoredState()
    {
        // Arrange
        var store = new InMemoryProjectionStore();
        var key = "PaymentProjection";
        var state = """{"paymentId":"PAY-001","status":"Completed"}""";
        await store.SaveAsync(key, Utf8(state));

        // Act
        await store.ClearAsync(key);
        var loaded = await store.LoadAsync(key);

        // Assert
        loaded.Should().BeNull();
    }

    /// <summary>A load hands out the stored copy, so it allocates nothing.</summary>
    [Fact]
    public async Task Load_ReturnsTheStoredCopyEachTime()
    {
        var store = new InMemoryProjectionStore();
        await store.SaveAsync("k", Utf8("state"));

        var first = (await store.LoadAsync("k"))!.Value;
        var second = (await store.LoadAsync("k"))!.Value;

        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(first, out var a).Should().BeTrue();
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(second, out var b).Should().BeTrue();
        b.Array.Should().BeSameAs(a.Array);
    }
}
