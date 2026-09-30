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

    // A failure that came before DisposeAsync requested the stop is not shutdown: it faults the
    // background task and DisposeAsync rethrows it. The test fails the handler, lets DisposeAsync
    // request the stop, and only then lets the subscription observe the failure, so the token is
    // already cancelled when the exception is caught. That ordering used to swallow it, see #434.
    [Fact]
    public async Task HandlerException_BeforeTheStop_ObservedAfterIt_PropagatesViaDisposeAsync()
    {
        var thrown = await DisposeAfterHandlerFailedFirst(new InvalidOperationException("handler boom"));

        thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("handler boom");
    }

    // An OperationCanceledException that the subscription did not cause, such as a handler's own
    // timeout, is a failure like any other; DisposeAsync used to swallow it as if it were shutdown.
    // Same ordering as above, so the result does not depend on when the subscription observes it.
    [Fact]
    public async Task HandlerOwnCancellation_PropagatesViaDisposeAsync()
    {
        using var handlerTimeout = new CancellationTokenSource();
        await handlerTimeout.CancelAsync();

        var thrown = await DisposeAfterHandlerFailedFirst(
            new OperationCanceledException("handler timed out", handlerTimeout.Token));

        thrown.Should().BeOfType<OperationCanceledException>().Which.Message.Should().Be("handler timed out");
    }

    // A handler that is still running when DisposeAsync requests the stop, and then fails, fails
    // because of the stop, whatever it throws: DisposeAsync does not throw.
    [Fact]
    public async Task HandlerException_AfterTheStop_IsShutdown()
    {
        var operation = new ControlledOperation();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = new PollingEventSubscription(
            new SingleEventAdapter(MakeRaw()), new StreamId("test-stream"), StreamPosition.Start,
            (_, ct) =>
            {
                ct.Register(() => stopped.TrySetResult());
                return operation.AsValueTask();
            },
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await operation.Awaited.WaitAsync(TimeSpan.FromSeconds(10));
        var dispose = sub.DisposeAsync().AsTask();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        operation.Fail(new InvalidOperationException("A severe error occurred on the current command."));
        operation.RunContinuation();

        await dispose.Invoking(t => t).Should().NotThrowAsync();
    }

    // A read that failed before the stop is reported the same way as a handler that did.
    [Fact]
    public async Task ReadException_BeforeTheStop_ObservedAfterIt_PropagatesViaDisposeAsync()
    {
        var read = new ControlledOperation();
        var adapter = new ControlledReadAdapter(read);
        var sub = new PollingEventSubscription(
            adapter, new StreamId("test-stream"), StreamPosition.Start,
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await read.Awaited.WaitAsync(TimeSpan.FromSeconds(10));
        read.Fail(new InvalidOperationException("store unavailable"));
        var dispose = sub.DisposeAsync().AsTask();
        await adapter.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        read.RunContinuation();

        var act = async () => await dispose;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("store unavailable");
    }

    /// <summary>
    /// Fails the handler, lets DisposeAsync request the stop, then lets the subscription observe
    /// the failure, and returns what DisposeAsync threw.
    /// </summary>
    private static async Task<Exception?> DisposeAfterHandlerFailedFirst(Exception failure)
    {
        var operation = new ControlledOperation();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = new PollingEventSubscription(
            new SingleEventAdapter(MakeRaw()), new StreamId("test-stream"), StreamPosition.Start,
            (_, ct) =>
            {
                ct.Register(() => stopped.TrySetResult());
                return operation.AsValueTask();
            },
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();
        await operation.Awaited.WaitAsync(TimeSpan.FromSeconds(10));
        operation.Fail(failure);
        var dispose = sub.DisposeAsync().AsTask();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        operation.RunContinuation();

        try
        {
            await dispose;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>An adapter whose read waits on a <see cref="ControlledOperation"/>.</summary>
    private sealed class ControlledReadAdapter(ControlledOperation read) : IEventStoreAdapter
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the token given to the read is cancelled.</summary>
        public Task Stopped => _stopped.Task;

        public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        {
            ct.Register(() => _stopped.TrySetResult());
            return new Events(read);
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();

        private sealed class Events(ControlledOperation read) : IAsyncEnumerable<RawEvent>, IAsyncEnumerator<RawEvent>
        {
            public RawEvent Current => throw new InvalidOperationException("No event is read.");

            public IAsyncEnumerator<RawEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync() => read.AsBoolValueTask();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
