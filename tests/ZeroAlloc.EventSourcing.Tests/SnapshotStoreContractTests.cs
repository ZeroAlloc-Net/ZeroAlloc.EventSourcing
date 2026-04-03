using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Contract tests for <see cref="ISnapshotStore{TState}"/>. Implementations should
/// inherit from this class and provide their own concrete <see cref="ISnapshotStore{TestState}"/> instance.
/// </summary>
public abstract class SnapshotStoreContractTests
{
    /// <summary>Test aggregate state matching the pattern of OrderState from aggregates.</summary>
    public struct TestState
    {
        /// <summary>Order identifier.</summary>
        public string OrderId { get; set; }

        /// <summary>Order amount.</summary>
        public int Amount { get; set; }
    }

    /// <summary>Subclasses override to provide a concrete snapshot store instance.</summary>
    protected abstract ISnapshotStore<TestState> CreateStore();

    /// <summary>Verify that reading a non-existent stream returns null.</summary>
    [Fact]
    public async Task Read_NoSnapshot_ReturnsNull()
    {
        var store = CreateStore();
        var streamId = new StreamId("order-123");

        var result = await store.ReadAsync(streamId);

        result.Should().BeNull();
    }

    /// <summary>Verify that written state can be read back with correct position and state values.</summary>
    [Fact]
    public async Task Write_ThenRead_ReturnsWrittenState()
    {
        var store = CreateStore();
        var streamId = new StreamId("order-123");
        var state = new TestState { OrderId = "123", Amount = 100 };
        var position = new StreamPosition(5);

        await store.WriteAsync(streamId, position, state);
        var result = await store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.OrderId.Should().Be("123");
        result.Value.State.Amount.Should().Be(100);
    }

    /// <summary>Verify that multiple writes to the same stream replace the previous snapshot (last-write-wins).</summary>
    [Fact]
    public async Task Write_Multiple_LastOneWins()
    {
        var store = CreateStore();
        var streamId = new StreamId("order-123");

        await store.WriteAsync(streamId, new StreamPosition(3), new TestState { Amount = 30 });
        await store.WriteAsync(streamId, new StreamPosition(5), new TestState { Amount = 50 });

        var result = await store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Value.Should().Be(5);
        result.Value.State.Amount.Should().Be(50);
    }

    /// <summary>Verify that snapshots for different streams are isolated and do not interfere.</summary>
    [Fact]
    public async Task Read_DifferentStreams_Isolated()
    {
        var store = CreateStore();
        var stream1 = new StreamId("order-1");
        var stream2 = new StreamId("order-2");

        await store.WriteAsync(stream1, new StreamPosition(5), new TestState { OrderId = "1", Amount = 100 });
        await store.WriteAsync(stream2, new StreamPosition(5), new TestState { OrderId = "2", Amount = 200 });

        var result1 = await store.ReadAsync(stream1);
        var result2 = await store.ReadAsync(stream2);

        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Value.State.OrderId.Should().Be("1");
        result2.Value.State.OrderId.Should().Be("2");
    }
}
