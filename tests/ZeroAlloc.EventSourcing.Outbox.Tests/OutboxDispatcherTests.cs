using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Outbox;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class OutboxDispatcherTests
{
    [Fact]
    public async Task Dispatcher_invokes_handler_for_INotification_event_then_advances_checkpoint()
    {
        var (store, checkpoints, recorder) = TestHarness.New();
        var opts = new OutboxOptions { ConsumerId = "test-1", PollInterval = TimeSpan.FromMilliseconds(50) };
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(42) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, deadLetters: null, opts, NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await TestHarness.WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle().Which.Should().BeEquivalentTo(new TestEventA(42));
        var pos = await checkpoints.ReadAsync("test-1");
        pos.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispatcher_skips_non_INotification_event()
    {
        var (store, checkpoints, recorder) = TestHarness.New();
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventNotNotification(7) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-2", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        // Positive signal: wait until the consumer has observed the event and advanced its
        // checkpoint past position 0. This proves the dispatcher polled — without it, an
        // empty Dispatched list could be a race (test running before the first poll cycle).
        await TestHarness.WaitUntil(async () => await checkpoints.ReadAsync("test-2") is { } pos && pos.Value > 0, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatcher_skips_excluded_INotification_type()
    {
        var (store, checkpoints, recorder) = TestHarness.New();
        var opts = new OutboxOptions { ConsumerId = "test-3", PollInterval = TimeSpan.FromMilliseconds(50) }
            .Exclude<TestEventA>();
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(1), new TestEventB("ok") }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, null, opts, NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await TestHarness.WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle().Which.Should().BeOfType<TestEventB>();
    }

    [Fact]
    public async Task Dispatcher_retries_on_transient_failure_then_succeeds()
    {
        var (store, checkpoints, recorder) = TestHarness.New();
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
        await TestHarness.WaitUntil(() => recorder.Dispatched.Count == 1, TimeSpan.FromSeconds(2));
        await sut.StopAsync(default);

        recorder.Dispatched.Should().ContainSingle();
        attemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Dispatcher_writes_to_DLQ_after_retry_exhaustion()
    {
        var (store, checkpoints, recorder) = TestHarness.New();
        var dlq = new InMemoryDeadLetterStore();
        recorder.ThrowFor = _ => new InvalidOperationException("poison");
        await store.AppendAsync(new StreamId("order-1"), new object[] { new TestEventA(666) }.AsMemory(), StreamPosition.Start);

        var sut = new OutboxDispatcher(store, checkpoints, recorder, dlq,
            new OutboxOptions { ConsumerId = "test-5", PollInterval = TimeSpan.FromMilliseconds(50), MaxRetries = 2 },
            NullLogger<OutboxDispatcher>.Instance);
        await sut.StartAsync(default);
        await TestHarness.WaitUntil(async () => await CountDlq(dlq) > 0, TimeSpan.FromSeconds(5));
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

    [Fact]
    public async Task Dispatcher_resumes_from_checkpoint_after_restart()
    {
        var (store, checkpoints, recorder) = TestHarness.New();
        await store.AppendAsync(new StreamId("order-1"),
            new object[] { new TestEventA(1), new TestEventA(2), new TestEventA(3) }.AsMemory(),
            StreamPosition.Start);

        // First run — drain
        var sut1 = new OutboxDispatcher(store, checkpoints, recorder, null,
            new OutboxOptions { ConsumerId = "test-6", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);
        await sut1.StartAsync(default);
        await TestHarness.WaitUntil(() => recorder.Dispatched.Count == 3, TimeSpan.FromSeconds(2));
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

    // Issue 421: stopping while a read is in flight made SqlClient throw a SqlException, which the
    // loop logged as a crash and rethrew out of StopAsync, because only OperationCanceledException
    // counted as shutdown. The adapter throws a non-cancellation exception the same way.
    [Fact]
    public async Task StopAsync_WhileReadInFlight_AdapterThrowsNonCancellationException_StopsCleanly()
    {
        var adapter = new ThrowsOnCancelAdapter();
        var store = TestHarness.NewEventStoreWithAdapter(adapter);
        var sut = new OutboxDispatcher(store, new InMemoryCheckpointStore(), new RecordingDispatcher(), null,
            new OutboxOptions { ConsumerId = "test-421", PollInterval = TimeSpan.FromMilliseconds(50) },
            NullLogger<OutboxDispatcher>.Instance);

        await sut.StartAsync(default);
        await adapter.ReadInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await sut.StopAsync(default);
        await act.Should().NotThrowAsync();
        await sut.DisposeAsync();
    }

    // A failure while the dispatcher is not stopping still halts it and surfaces from StopAsync.
    [Fact]
    public async Task StopAsync_AfterReadFailedWhileRunning_Rethrows()
    {
        var adapter = new ThrowsOnCancelAdapter { FailWith = new InvalidOperationException("store unavailable") };
        var store = TestHarness.NewEventStoreWithAdapter(adapter);
        var logger = new CrashSignallingLogger();
        var sut = new OutboxDispatcher(store, new InMemoryCheckpointStore(), new RecordingDispatcher(), null,
            new OutboxOptions { ConsumerId = "test-421-fail", PollInterval = TimeSpan.FromMilliseconds(50) },
            logger);

        await sut.StartAsync(default);
        // The loop logs the crash only after deciding it is not shutdown, so StopAsync cannot race it.
        await logger.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await sut.StopAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("store unavailable");
    }

    // An OperationCanceledException the dispatcher did not cause, such as a store timeout, is a
    // crash like any other; StopAsync used to swallow it as if it were shutdown.
    [Fact]
    public async Task StopAsync_AfterForeignCancellationWhileRunning_Rethrows()
    {
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var adapter = new ThrowsOnCancelAdapter
        {
            FailWith = new OperationCanceledException("store timed out", foreign.Token),
        };
        var store = TestHarness.NewEventStoreWithAdapter(adapter);
        var logger = new CrashSignallingLogger();
        var sut = new OutboxDispatcher(store, new InMemoryCheckpointStore(), new RecordingDispatcher(), null,
            new OutboxOptions { ConsumerId = "test-421-foreign", PollInterval = TimeSpan.FromMilliseconds(50) },
            logger);

        await sut.StartAsync(default);
        await logger.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await sut.StopAsync(default);
        await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("store timed out");
    }

    private sealed class CrashSignallingLogger : Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>
    {
        public TaskCompletionSource Crashed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
                Crashed.TrySetResult();
        }
    }

    private sealed class ThrowsOnCancelAdapter : IEventStoreAdapter
    {
        public TaskCompletionSource ReadInFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? FailWith { get; init; }

        public async IAsyncEnumerable<RawEvent> ReadAsync(
            StreamId id,
            StreamPosition from,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (FailWith is not null)
                throw FailWith;
            ReadInFlight.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => cancelled.TrySetResult()))
                await cancelled.Task.ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new InvalidOperationException("A severe error occurred on the current command.");
            yield break;
        }

        public ValueTask<ZeroAlloc.Results.Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
