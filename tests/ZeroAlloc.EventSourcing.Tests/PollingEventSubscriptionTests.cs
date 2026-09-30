using System.Text;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Unit tests for <see cref="PollingEventSubscription"/> using an inline fake adapter —
/// no Testcontainers required.
/// </summary>
public sealed class PollingEventSubscriptionTests
{
    private static RawEvent MakeRaw(string eventType = "TestEvent")
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        return new RawEvent(new StreamPosition(1), eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    /// <summary>
    /// Fake adapter that yields a single pre-built event on the first ReadAsync call.
    /// Subsequent calls yield nothing, simulating an empty live tail.
    /// </summary>
    private sealed class SingleEventAdapter : IEventStoreAdapter
    {
        private readonly RawEvent _event;
        private int _readCount;

        public SingleEventAdapter(RawEvent @event) => _event = @event;

        public async IAsyncEnumerable<RawEvent> ReadAsync(
            StreamId id,
            StreamPosition from,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_readCount++ == 0)
                yield return _event;
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Fake adapter whose read signals that it is in flight, then blocks until its token is
    /// cancelled and throws <typeparamref name="TException"/>, the way SqlClient aborts a running
    /// command with a <c>SqlException</c> instead of an <see cref="OperationCanceledException"/>.
    /// </summary>
    private sealed class ThrowsOnCancelAdapter : IEventStoreAdapter
    {
        public TaskCompletionSource ReadInFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RawEvent> ReadAsync(
            StreamId id,
            StreamPosition from,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadInFlight.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => cancelled.TrySetResult()))
                await cancelled.Task.ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new InvalidOperationException("A severe error occurred on the current command.");
            yield break;
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // Issue 421: disposing while a read is in flight made SqlClient throw a SqlException, which
    // escaped DisposeAsync because only OperationCanceledException counted as shutdown.
    [Fact]
    public async Task DisposeAsync_WhileReadInFlight_AdapterThrowsNonCancellationException_DoesNotThrow()
    {
        var adapter = new ThrowsOnCancelAdapter();
        var sub = new PollingEventSubscription(
            adapter, new StreamId("test-stream"), StreamPosition.Start,
            (_, _) => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await adapter.ReadInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await sub.DisposeAsync();
        await act.Should().NotThrowAsync();
        sub.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WhileHandlerInFlight_HandlerThrowsNonCancellationException_DoesNotThrow()
    {
        var adapter = new SingleEventAdapter(MakeRaw());
        var handlerInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = new PollingEventSubscription(
            adapter, new StreamId("test-stream"), StreamPosition.Start,
            async (_, ct) =>
            {
                handlerInFlight.TrySetResult();
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (ct.Register(() => cancelled.TrySetResult()))
                    await cancelled.Task.ConfigureAwait(false);
                throw new InvalidOperationException("handler aborted by shutdown");
            },
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await handlerInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = async () => await sub.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // A failure while the subscription is not being disposed is not shutdown: it faults the
    // background task and DisposeAsync rethrows it. The handler's failure is completed by the test
    // itself, so the subscription has observed it before DisposeAsync cancels.
    [Fact]
    public async Task HandlerException_PropagatesViaDisposeAsync()
    {
        var id = new StreamId("test-stream");
        var adapter = new SingleEventAdapter(MakeRaw());
        var handlerCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Without RunContinuationsAsynchronously, SetException below runs the subscription's
        // continuations inline, so the failure is handled before SetException returns.
        var handlerResult = new TaskCompletionSource();

        var sub = new PollingEventSubscription(
            adapter, id, StreamPosition.Start,
            (_, _) =>
            {
                handlerCalled.TrySetResult();
                return new ValueTask(handlerResult.Task);
            },
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await handlerCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handlerResult.SetException(new InvalidOperationException("handler boom"));

        var act = async () => await sub.DisposeAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler boom");
    }

    // An OperationCanceledException that the subscription did not cause, such as a handler's own
    // timeout, is a failure like any other; DisposeAsync used to swallow it as if it were shutdown.
    [Fact]
    public async Task HandlerOwnCancellation_PropagatesViaDisposeAsync()
    {
        var adapter = new SingleEventAdapter(MakeRaw());
        var handlerCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerResult = new TaskCompletionSource();

        var sub = new PollingEventSubscription(
            adapter, new StreamId("test-stream"), StreamPosition.Start,
            (_, _) =>
            {
                handlerCalled.TrySetResult();
                return new ValueTask(handlerResult.Task);
            },
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await handlerCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var handlerTimeout = new CancellationTokenSource();
        await handlerTimeout.CancelAsync();
        handlerResult.SetException(new OperationCanceledException("handler timed out", handlerTimeout.Token));

        var act = async () => await sub.DisposeAsync();
        await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("handler timed out");
    }

    // The in-memory adapter keeps the position it is given, as EventStore sets it; SQL adapters assign their own.
    private static RawEvent Raw(string eventType, long position)
        => MakeRaw(eventType) with { Position = new StreamPosition(position) };

    // Issue 409: after delivering the event at position N the subscription read from N + 1,
    // and reads exclude their start position, so the first event of every later poll cycle
    // was lost. Each append below lands in its own poll cycle.
    [Fact]
    public async Task Subscribe_DeliversEveryLiveEvent_AcrossPollCycles()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await adapter.AppendAsync(id, new[] { Raw("E1", 1) }.AsMemory(), StreamPosition.Start);

        var received = new List<string>();
        var sub = new PollingEventSubscription(adapter, id, StreamPosition.Start, (e, _) =>
        {
            lock (received) received.Add(e.EventType);
            return ValueTask.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));
        await sub.StartAsync();
        try
        {
            await PollingTestHelpers.WaitForCountAsync(received, 1);
            await adapter.AppendAsync(id, new[] { Raw("E2", 2) }.AsMemory(), new StreamPosition(1));
            await PollingTestHelpers.WaitForCountAsync(received, 2);
            await adapter.AppendAsync(id, new[] { Raw("E3", 3) }.AsMemory(), new StreamPosition(2));
            await PollingTestHelpers.WaitForCountAsync(received, 3);
        }
        finally
        {
            await sub.DisposeAsync();
        }

        received.Should().Equal("E1", "E2", "E3");
    }

    [Fact]
    public async Task Subscribe_Global_DeliversEveryLiveEvent_AcrossPollCycles()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var a = new StreamId($"a-{Guid.NewGuid()}");
        var b = new StreamId($"b-{Guid.NewGuid()}");
        await adapter.AppendAsync(a, new[] { Raw("A1", 1) }.AsMemory(), StreamPosition.Start);

        var received = new List<string>();
        var sub = new PollingEventSubscription(adapter, StreamId.Global, StreamPosition.Start, (e, _) =>
        {
            lock (received) received.Add(e.EventType);
            return ValueTask.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));
        await sub.StartAsync();
        try
        {
            await PollingTestHelpers.WaitForCountAsync(received, 1);
            await adapter.AppendAsync(b, new[] { Raw("B1", 1) }.AsMemory(), StreamPosition.Start);
            await PollingTestHelpers.WaitForCountAsync(received, 2);
            await adapter.AppendAsync(a, new[] { Raw("A2", 2) }.AsMemory(), new StreamPosition(1));
            await PollingTestHelpers.WaitForCountAsync(received, 3);
        }
        finally
        {
            await sub.DisposeAsync();
        }

        received.Should().Equal("A1", "B1", "A2");
    }
}
