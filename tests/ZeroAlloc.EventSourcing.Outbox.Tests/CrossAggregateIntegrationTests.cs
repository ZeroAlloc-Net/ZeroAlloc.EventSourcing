using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Outbox;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class CrossAggregateIntegrationTests
{
    [Fact]
    public async Task Handler_triggered_by_outbox_saves_new_event_that_outbox_picks_up_on_next_poll()
    {
        // Reuse the canonical NewHarness pattern from OutboxDispatcherTests (the helper is
        // marked internal static specifically for cross-class reuse).
        var (store, checkpoints, recorder) = OutboxDispatcherTests.NewHarness();

        // Pre-seed the credit-1 stream so its per-stream position counter is in lock-step
        // with order-1's. The InMemoryEventStoreAdapter's "*" pseudo-stream reads each
        // underlying stream from `Skip(checkpoint)`, treating per-stream version as if it
        // were a global cursor — so a fresh stream whose only event lands at position 1
        // would be silently skipped once the checkpoint has advanced past 1. Seeding a
        // non-INotification placeholder at position 1 represents prior aggregate history
        // and aligns the position counters across both streams. The placeholder itself is
        // filtered out by the INotification check in OutboxDispatcher.DispatchAsync.
        await store.AppendAsync(
            new StreamId("credit-1"),
            new object[] { new TestEventNotNotification(0) }.AsMemory(),
            StreamPosition.Start);

        // The handler-as-OnDispatch: when the dispatcher delivers a TestEventA, append a
        // TestEventB to a SEPARATE stream (credit-1). With the pre-seed above the new
        // TestEventB lands at position 2, which the consumer surfaces on its next poll
        // once the checkpoint has advanced past 1 (driven by the order-1 TestEventA).
        recorder.OnDispatch = async ev =>
        {
            if (ev is TestEventA a)
            {
                await store.AppendAsync(
                    new StreamId("credit-1"),
                    new object[] { new TestEventB($"credited-{a.Value}") }.AsMemory(),
                    new StreamPosition(1)).ConfigureAwait(false);
            }
        };

        // Seed: append the initial event on the order stream.
        await store.AppendAsync(
            new StreamId("order-1"),
            new object[] { new TestEventA(50) }.AsMemory(),
            StreamPosition.Start);

        var sut = new OutboxDispatcher(
            store, checkpoints, recorder, deadLetters: null,
            new OutboxOptions { ConsumerId = "test-cross", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);

        await sut.StartAsync(default);
        await WaitUntil(
            () => recorder.Dispatched.OfType<TestEventB>().Any(),
            TimeSpan.FromSeconds(3));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().Contain(e => e is TestEventA);
        recorder.Dispatched.Should().Contain(e => e is TestEventB);
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25).ConfigureAwait(false);
        if (!predicate())
            throw new TimeoutException($"Predicate did not become true within {timeout}");
    }
}
