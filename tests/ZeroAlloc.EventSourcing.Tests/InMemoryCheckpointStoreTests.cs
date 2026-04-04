using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class InMemoryCheckpointStoreTests : CheckpointStoreContractTests
{
    protected override ICheckpointStore CreateStore() => new InMemoryCheckpointStore();

    [Fact]
    public async Task IsThreadSafe_ConcurrentWrites()
    {
        var store = new InMemoryCheckpointStore();
        var tasks = Enumerable.Range(0, 10)
            .Select(async i =>
            {
                await store.WriteAsync($"consumer-{i}", new StreamPosition(i * 10), default);
            });

        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
        {
            var pos = await store.ReadAsync($"consumer-{i}", default);
            pos.Should().NotBeNull();
            pos.Value.Value.Should().Be(i * 10);
        }
    }
}
