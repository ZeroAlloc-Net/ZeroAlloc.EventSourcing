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
        // Reuse the canonical harness wiring from TestHarness (shared across the suite).
        var (store, checkpoints, recorder) = TestHarness.New();

        // The handler-as-OnDispatch: when the dispatcher delivers a TestEventA, append a
        // TestEventB to a SEPARATE stream (credit-1). The InMemory adapter's fixed "*"
        // cursor surfaces the new TestEventB on the next poll once the global checkpoint
        // has advanced past the TestEventA (driven by the order-1 stream).
        recorder.OnDispatch = async ev =>
        {
            if (ev is TestEventA a)
            {
                await store.AppendAsync(
                    new StreamId("credit-1"),
                    new object[] { new TestEventB($"credited-{a.Value}") }.AsMemory(),
                    StreamPosition.Start).ConfigureAwait(false);
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
        await TestHarness.WaitUntil(
            () => recorder.Dispatched.OfType<TestEventB>().Any(),
            TimeSpan.FromSeconds(3));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().Contain(e => e is TestEventA);
        recorder.Dispatched.Should().Contain(e => e is TestEventB);
    }
}
