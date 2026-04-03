using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Test aggregate state for contract testing.
/// </summary>
public struct SnapshotTestState
{
    /// <summary>Order identifier.</summary>
    public string OrderId { get; set; }

    /// <summary>Order amount.</summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// Contract tests for <see cref="ISnapshotStore{TState}"/> SQL implementations.
/// Provides a shared set of tests that all SQL snapshot store implementations must pass.
/// Implementations inherit from this class and provide a concrete <see cref="ISnapshotStore{SnapshotTestState}"/> instance.
/// </summary>
public abstract class SnapshotStoreContractTests<TStore> : IAsyncLifetime
    where TStore : ISnapshotStore<SnapshotTestState>
{

    /// <summary>The snapshot store instance created and initialized by the test.</summary>
    protected TStore _store = default!;

    /// <summary>
    /// Subclasses override to create a concrete snapshot store instance and ensure the schema is initialized.
    /// </summary>
    protected abstract Task<TStore> CreateStoreAsync();

    /// <summary>
    /// Called before each test run. Creates the store and initializes schema.
    /// </summary>
    public async Task InitializeAsync()
    {
        _store = await CreateStoreAsync();
    }

    /// <summary>
    /// Called after each test run. Override to clean up resources (connections, containers, etc.).
    /// </summary>
    public abstract Task DisposeAsync();

    /// <summary>Verify that reading a non-existent stream returns null.</summary>
    [Fact]
    public async Task Read_NoSnapshot_ReturnsNull()
    {
        var streamId = new StreamId("nonexistent-123");
        var result = await _store.ReadAsync(streamId);
        result.Should().BeNull();
    }

    /// <summary>Verify that written state can be read back with correct position and state values.</summary>
    [Fact]
    public async Task Write_ThenRead_ReturnsWrittenState()
    {
        var streamId = new StreamId("order-123");
        var state = new SnapshotTestState { OrderId = "123", Amount = 100m };
        var position = new StreamPosition(5);

        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.OrderId.Should().Be("123");
        result.Value.State.Amount.Should().Be(100m);
    }

    /// <summary>Verify that multiple writes to the same stream replace the previous snapshot (last-write-wins).</summary>
    [Fact]
    public async Task Write_Multiple_LastOneWins()
    {
        var streamId = new StreamId("order-456");

        await _store.WriteAsync(streamId, new StreamPosition(3), new SnapshotTestState { OrderId = "456", Amount = 30m });
        await _store.WriteAsync(streamId, new StreamPosition(5), new SnapshotTestState { OrderId = "456", Amount = 50m });

        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Value.Should().Be(5);
        result.Value.State.Amount.Should().Be(50m);
    }

    /// <summary>Verify that snapshots for different streams are isolated and do not interfere.</summary>
    [Fact]
    public async Task Read_DifferentStreams_Isolated()
    {
        var stream1 = new StreamId("order-1");
        var stream2 = new StreamId("order-2");

        await _store.WriteAsync(stream1, new StreamPosition(5), new SnapshotTestState { OrderId = "1", Amount = 100m });
        await _store.WriteAsync(stream2, new StreamPosition(5), new SnapshotTestState { OrderId = "2", Amount = 200m });

        var result1 = await _store.ReadAsync(stream1);
        var result2 = await _store.ReadAsync(stream2);

        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Value.State.OrderId.Should().Be("1");
        result1.Value.State.Amount.Should().Be(100m);
        result2.Value.State.OrderId.Should().Be("2");
        result2.Value.State.Amount.Should().Be(200m);
    }
}
