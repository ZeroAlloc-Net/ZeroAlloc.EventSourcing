using FluentAssertions;
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
    public async Task Read_NonExistentConsumer_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.ReadAsync("nonexistent-consumer");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Write_NewPosition_SuccessfullyStored()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-1";
        var position = new StreamPosition(42);

        await store.WriteAsync(consumerId, position);
        var result = await store.ReadAsync(consumerId);

        result.Should().NotBeNull();
        result.Value.Value.Should().Be(position.Value);
    }

    [Fact]
    public async Task Write_UpdateExistingPosition_LastWriteWins()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-2";

        await store.WriteAsync(consumerId, new StreamPosition(10));
        await store.WriteAsync(consumerId, new StreamPosition(20));
        var result = await store.ReadAsync(consumerId);

        result.Value.Value.Should().Be(20);
    }

    [Fact]
    public async Task Delete_ExistingConsumer_ResetToNull()
    {
        var store = CreateStore();
        var consumerId = "test-consumer-3";

        await store.WriteAsync(consumerId, new StreamPosition(15));
        await store.DeleteAsync(consumerId);
        var result = await store.ReadAsync(consumerId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonExistentConsumer_DoesNotThrow()
    {
        var store = CreateStore();
        var exception = await Record.ExceptionAsync(() => store.DeleteAsync("nonexistent"));
        exception.Should().BeNull();
    }

    [Fact]
    public async Task MultipleConsumers_IndependentPositions()
    {
        var store = CreateStore();

        await store.WriteAsync("consumer-a", new StreamPosition(10));
        await store.WriteAsync("consumer-b", new StreamPosition(20));

        var posA = await store.ReadAsync("consumer-a");
        var posB = await store.ReadAsync("consumer-b");

        posA.Value.Value.Should().Be(10);
        posB.Value.Value.Should().Be(20);
    }
}
