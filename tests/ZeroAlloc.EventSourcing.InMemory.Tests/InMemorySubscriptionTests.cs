using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class InMemorySubscriptionTests
{
    private static RawEvent MakeRaw(StreamPosition pos, string type = "TestEvent")
        => new(pos, type, new byte[] { 1 }, EventMetadata.New(type));

    [Fact]
    public async Task Subscribe_ReceivesEventsAppendedAfterSubscription()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var received = new List<RawEvent>();
        var tcs = new TaskCompletionSource();

        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start, (e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await sub.StartAsync();

        await adapter.AppendAsync(id, new[] { MakeRaw(StreamPosition.Start) }.AsMemory(), StreamPosition.Start);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)); // deterministic, 5s timeout for CI safety

        received.Should().HaveCount(1);
    }

    [Fact]
    public async Task Subscribe_AfterDispose_StopsReceivingEvents()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");
        var received = new List<RawEvent>();

        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start,
            (e, _) => { received.Add(e); return ValueTask.CompletedTask; });
        await sub.StartAsync();
        await sub.DisposeAsync();

        await adapter.AppendAsync(id, new[] { MakeRaw(StreamPosition.Start) }.AsMemory(), StreamPosition.Start);
        await Task.Delay(10); // brief wait — handler should NOT fire; any reasonable delay suffices

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscription_IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId("orders-1");

        var sub = await adapter.SubscribeAsync(id, StreamPosition.Start, (_, _) => ValueTask.CompletedTask);

        sub.IsRunning.Should().BeFalse();
        await sub.StartAsync();
        sub.IsRunning.Should().BeTrue();
        await sub.DisposeAsync();
        sub.IsRunning.Should().BeFalse();
    }
}
