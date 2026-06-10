using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Outbox;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class OutboxDispatcherTests
{
    internal sealed class OutboxJsonSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
    }

    internal sealed class OutboxTestTypeRegistry : IEventTypeRegistry
    {
        private readonly Dictionary<string, Type> _map = new()
        {
            [nameof(TestEventA)] = typeof(TestEventA),
            [nameof(TestEventB)] = typeof(TestEventB),
            [nameof(TestEventNotNotification)] = typeof(TestEventNotNotification),
        };

        public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);

        public string GetTypeName(Type type) => type.Name;
    }

    internal static (IEventStore store, ICheckpointStore checkpoints, RecordingDispatcher dispatcher) NewHarness()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var store = new EventStore(adapter, new OutboxJsonSerializer(), new OutboxTestTypeRegistry());
        var checkpoints = new InMemoryCheckpointStore();
        var dispatcher = new RecordingDispatcher();
        return (store, checkpoints, dispatcher);
    }

    internal static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25).ConfigureAwait(false);
        if (!predicate())
            throw new TimeoutException($"Predicate did not become true within {timeout}");
    }

    [Fact]
    public async Task Dispatcher_invokes_handler_for_INotification_event_then_advances_checkpoint()
    {
        var (store, checkpoints, recorder) = NewHarness();
        var opts = new OutboxOptions { ConsumerId = "test-1", PollInterval = TimeSpan.FromMilliseconds(50) };
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(42) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, deadLetters: null, opts, NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle().Which.Should().BeEquivalentTo(new TestEventA(42));
        var pos = await checkpoints.ReadAsync("test-1");
        pos.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispatcher_skips_non_INotification_event()
    {
        var (store, checkpoints, recorder) = NewHarness();
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventNotNotification(7) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-2", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        // Positive signal: wait until the consumer has observed the event and advanced its
        // checkpoint past position 0. This proves the dispatcher polled — without it, an
        // empty Dispatched list could be a race (test running before the first poll cycle).
        await WaitUntil(async () => await checkpoints.ReadAsync("test-2") is { } pos && pos.Value > 0, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatcher_skips_excluded_INotification_type()
    {
        var (store, checkpoints, recorder) = NewHarness();
        var opts = new OutboxOptions { ConsumerId = "test-3", PollInterval = TimeSpan.FromMilliseconds(50) }
            .Exclude<TestEventA>();
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(1), new TestEventB("ok") }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, null, opts, NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle().Which.Should().BeOfType<TestEventB>();
    }

    [Fact]
    public async Task Dispatcher_retries_on_transient_failure_then_succeeds()
    {
        var (store, checkpoints, recorder) = NewHarness();
        var attemptCount = 0;
        recorder.ThrowFor = ev =>
        {
            attemptCount++;
            return attemptCount < 2 ? new InvalidOperationException("transient") : null;
        };
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(99) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-4", PollInterval = TimeSpan.FromMilliseconds(50), MaxRetries = 3 },
            NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle();
        attemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Dispatcher_writes_to_DLQ_after_retry_exhaustion()
    {
        var (store, checkpoints, recorder) = NewHarness();
        var dlq = new InMemoryDeadLetterStore();
        recorder.ThrowFor = _ => new InvalidOperationException("poison");
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(666) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, dlq,
            new OutboxOptions { ConsumerId = "test-5", PollInterval = TimeSpan.FromMilliseconds(50), MaxRetries = 2 },
            NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await WaitUntil(async () => await CountDlq(dlq) > 0, TimeSpan.FromSeconds(5));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().BeEmpty();
        (await CountDlq(dlq)).Should().Be(1);
    }

    private static async Task<int> CountDlq(InMemoryDeadLetterStore dlq)
    {
        var count = 0;
        await foreach (var _ in dlq.ReadAllAsync())
            count++;
        return count;
    }

    private static async Task WaitUntil(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await predicate().ConfigureAwait(false) && DateTime.UtcNow < deadline)
            await Task.Delay(25).ConfigureAwait(false);
        if (!await predicate().ConfigureAwait(false))
            throw new TimeoutException($"Predicate did not become true within {timeout}");
    }

    [Fact]
    public async Task Dispatcher_resumes_from_checkpoint_after_restart()
    {
        var (store, checkpoints, recorder) = NewHarness();
        await store.AppendAsync(new StreamId("order-1"),
            new object[] { new TestEventA(1), new TestEventA(2), new TestEventA(3) }.AsMemory(),
            StreamPosition.Start);

        // First run — drain
        var sut1 = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-6", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);
        await sut1.StartAsync(default);
        await WaitUntil(() => recorder.Dispatched.Count == 3, TimeSpan.FromSeconds(2));
        await sut1.StopAsync(default);

        // Capture the checkpoint position the first dispatcher landed on. The second run
        // should leave it untouched, since there are no new events past that position.
        var checkpointAfterFirstRun = await checkpoints.ReadAsync("test-6");
        checkpointAfterFirstRun.Should().NotBeNull();

        // Second run on the SAME stores — should deliver 0 new events
        var sut2 = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-6", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);
        await sut2.StartAsync(default);
        // Wait several poll intervals so the dispatcher has demonstrably tried and found
        // nothing. The negative assertion is intrinsically time-based; 500ms over a 50ms
        // poll interval gives ~10 cycles to surface any spurious dispatch.
        await Task.Delay(500);
        await sut2.StopAsync(default);

        recorder.Dispatched.Count.Should().Be(3);   // unchanged
        // Stricter invariant: the checkpoint must not have advanced either.
        var checkpointAfterSecondRun = await checkpoints.ReadAsync("test-6");
        checkpointAfterSecondRun.Should().Be(checkpointAfterFirstRun);
    }
}
