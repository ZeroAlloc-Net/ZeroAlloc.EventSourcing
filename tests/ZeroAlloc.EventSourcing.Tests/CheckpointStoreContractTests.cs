using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Contract tests for ICheckpointStore implementations.
/// All implementations must pass these tests.
/// </summary>
public abstract class CheckpointStoreContractTests
{
    protected abstract ICheckpointStore CreateStore();

    [Fact]
    public async Task ReadAsync_NonExistentConsumer_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.ReadAsync("nonexistent-consumer");
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAsync_NewPosition_SuccessfullyStored()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-1";
        var position = new StreamPosition(42);

        await store.WriteAsync(consumerId, position);
        var result = await store.ReadAsync(consumerId);

        Assert.NotNull(result);
        Assert.Equal(position.Value, result.Value.Value);
    }

    [Fact]
    public async Task WriteAsync_UpdateExistingPosition_LastWriteWins()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-2";

        await store.WriteAsync(consumerId, new StreamPosition(10));
        await store.WriteAsync(consumerId, new StreamPosition(20));
        var result = await store.ReadAsync(consumerId);

        Assert.Equal(20, result.Value.Value);
    }

    [Fact]
    public async Task DeleteAsync_ExistingConsumer_ResetToNull()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-3";

        await store.WriteAsync(consumerId, new StreamPosition(15));
        await store.DeleteAsync(consumerId);
        var result = await store.ReadAsync(consumerId);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentConsumer_DoesNotThrow()
    {
        var store = CreateStore();
        var exception = await Record.ExceptionAsync(() => store.DeleteAsync("nonexistent"));
        Assert.Null(exception);
    }

    [Fact]
    public async Task MultipleConsumers_IndependentPositions()
    {
        var store = CreateStore();

        await store.WriteAsync("consumer-a", new StreamPosition(10));
        await store.WriteAsync("consumer-b", new StreamPosition(20));

        var posA = await store.ReadAsync("consumer-a");
        var posB = await store.ReadAsync("consumer-b");

        Assert.Equal(10, posA.Value.Value);
        Assert.Equal(20, posB.Value.Value);
    }
}
