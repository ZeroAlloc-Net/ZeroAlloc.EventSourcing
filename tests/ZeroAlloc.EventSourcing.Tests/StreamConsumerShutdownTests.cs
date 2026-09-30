using AwesomeAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// A handler that is still running when the consumer's token is cancelled, and then fails, is
/// shutdown, not a failing event: the consumer stops with an <see cref="OperationCanceledException"/>, the error
/// strategy is not applied, and the checkpoint does not move past the event, so it is handled again
/// on the next start. A failure while running still follows the error strategy. See #422.
/// </summary>
/// <remarks>
/// No test depends on timing. The handler signals that it is running, the test then cancels, and
/// the handler fails only once it has observed the cancellation.
/// </remarks>
public class StreamConsumerShutdownTests
{
    private readonly IEventStore _eventStore = new EventStore(
        new InMemoryEventStoreAdapter(),
        new JsonEventSerializer(),
        new StreamConsumerTestEventTypeRegistry());

    private readonly InMemoryCheckpointStore _checkpointStore = new();
    private readonly InMemoryDeadLetterStore _deadLetterStore = new();

    [Theory]
    [InlineData(ErrorHandlingStrategy.Skip, CommitStrategy.AfterEvent)]
    [InlineData(ErrorHandlingStrategy.Skip, CommitStrategy.AfterBatch)]
    [InlineData(ErrorHandlingStrategy.DeadLetter, CommitStrategy.AfterEvent)]
    [InlineData(ErrorHandlingStrategy.DeadLetter, CommitStrategy.AfterBatch)]
    public async Task HandlerFailingAfterCancellation_StopsWithoutApplyingTheErrorStrategy(
        ErrorHandlingStrategy strategy, CommitStrategy commit)
    {
        var streamId = await AppendAsync("shutdown-" + strategy + "-" + commit, 2);
        var consumer = Consumer(streamId, new StreamConsumerOptions
        {
            MaxRetries = 0, ErrorStrategy = strategy, CommitStrategy = commit,
        });
        using var cts = new CancellationTokenSource();
        var running = NewSignal();
        var handled = new List<long>();

        var consume = consumer.ConsumeAsync(async (envelope, ct) =>
        {
            handled.Add(envelope.Position.Value);
            running.TrySetResult();
            await FailOnceCancelledAsync(ct);
        }, cts.Token);

        await running.Task;
        cts.Cancel();

        var thrown = await consume.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        handled.Should().Equal(1L);
        (await _checkpointStore.ReadAsync(consumer.ConsumerId)).Should().BeNull();
        (await DeadLettersAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// The event whose handler was stopped is handled again when the consumer starts next.
    /// </summary>
    [Fact]
    public async Task EventStoppedByShutdown_IsHandledAgainOnTheNextStart()
    {
        var streamId = await AppendAsync("shutdown-restart", 2);
        var options = new StreamConsumerOptions
        {
            MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.Skip, CommitStrategy = CommitStrategy.AfterEvent,
        };
        using var cts = new CancellationTokenSource();
        var running = NewSignal();

        var first = Consumer(streamId, options).ConsumeAsync(async (_, ct) =>
        {
            running.TrySetResult();
            await FailOnceCancelledAsync(ct);
        }, cts.Token);
        await running.Task;
        cts.Cancel();
        await first.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        var handled = new List<long>();
        await Consumer(streamId, options).ConsumeAsync((envelope, _) =>
        {
            handled.Add(envelope.Position.Value);
            return Task.CompletedTask;
        });

        handled.Should().Equal(1L, 2L);
    }

    /// <summary>
    /// A handler that honours the token with an <see cref="OperationCanceledException"/> is
    /// shutdown too. It is reported as a cancellation of the caller's token, not dead-lettered.
    /// </summary>
    [Fact]
    public async Task HandlerCancelledByTheConsumersToken_IsRethrownAndNotDeadLettered()
    {
        var streamId = await AppendAsync("shutdown-oce", 1);
        var consumer = Consumer(streamId, new StreamConsumerOptions
        {
            MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.DeadLetter,
        });
        using var cts = new CancellationTokenSource();
        var running = NewSignal();

        var consume = consumer.ConsumeAsync(async (_, ct) =>
        {
            running.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, cts.Token);
        await running.Task;
        cts.Cancel();

        var thrown = await consume.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token);
        (await DeadLettersAsync()).Should().BeEmpty();
        (await _checkpointStore.ReadAsync(consumer.ConsumerId)).Should().BeNull();
    }

    /// <summary>
    /// Shutdown during the backoff before a retry stops the consumer; it does not use up the
    /// retries and apply the error strategy.
    /// </summary>
    [Fact]
    public async Task CancellationDuringRetryBackoff_StopsWithoutApplyingTheErrorStrategy()
    {
        var streamId = await AppendAsync("shutdown-backoff", 1);
        var consumer = Consumer(streamId, new StreamConsumerOptions
        {
            MaxRetries = 1,
            RetryPolicy = new ExponentialBackoffRetryPolicy(initialDelayMs: 600_000, maxDelayMs: 600_000),
            ErrorStrategy = ErrorHandlingStrategy.DeadLetter,
            CommitStrategy = CommitStrategy.AfterEvent,
        });
        using var cts = new CancellationTokenSource();
        var failed = NewSignal();
        var calls = 0;

        var consume = consumer.ConsumeAsync((_, _) =>
        {
            calls++;
            failed.TrySetResult();
            throw new InvalidOperationException("transient");
        }, cts.Token);
        await failed.Task;
        cts.Cancel();

        await consume.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
        (await DeadLettersAsync()).Should().BeEmpty();
        (await _checkpointStore.ReadAsync(consumer.ConsumerId)).Should().BeNull();
    }

    [Fact]
    public async Task HandlerFailingWhileRunning_WithALiveToken_IsSkippedAndCheckpointed()
    {
        var streamId = await AppendAsync("running-skip", 2);
        var consumer = Consumer(streamId, new StreamConsumerOptions
        {
            MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.Skip, CommitStrategy = CommitStrategy.AfterEvent,
        });
        using var cts = new CancellationTokenSource();
        var handled = new List<long>();

        await consumer.ConsumeAsync((envelope, _) =>
        {
            handled.Add(envelope.Position.Value);
            if (envelope.Position.Value == 1) throw new InvalidOperationException("poison");
            return Task.CompletedTask;
        }, cts.Token);

        handled.Should().Equal(1L, 2L);
        (await _checkpointStore.ReadAsync(consumer.ConsumerId)).Should().Be(new StreamPosition(2));
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> the consumer did not cause, such as a handler's
    /// own timeout, is a handler failure and is dead-lettered as before.
    /// </summary>
    [Fact]
    public async Task HandlerFailingWhileRunning_WithALiveToken_IsDeadLettered()
    {
        var streamId = await AppendAsync("running-dl", 2);
        var consumer = Consumer(streamId, new StreamConsumerOptions
        {
            MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.DeadLetter, CommitStrategy = CommitStrategy.AfterEvent,
        });
        using var cts = new CancellationTokenSource();

        await consumer.ConsumeAsync((envelope, _) =>
        {
            if (envelope.Position.Value == 1) throw new OperationCanceledException("handler timeout");
            return Task.CompletedTask;
        }, cts.Token);

        var entries = await DeadLettersAsync();
        entries.Should().ContainSingle();
        entries[0].ExceptionType.Should().Be(nameof(OperationCanceledException));
        (await _checkpointStore.ReadAsync(consumer.ConsumerId)).Should().Be(new StreamPosition(2));
    }

    private StreamConsumer Consumer(StreamId streamId, StreamConsumerOptions options) =>
        new(_eventStore, _checkpointStore, "consumer-" + streamId.Value, options, streamId, _deadLetterStore);

    private async Task<StreamId> AppendAsync(string name, int count)
    {
        var streamId = new StreamId(name);
        var events = new object[count];
        for (var i = 0; i < count; i++) events[i] = new TestEvent { Value = i };
        await _eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);
        return streamId;
    }

    private async Task<List<DeadLetterEntry>> DeadLettersAsync()
    {
        var entries = new List<DeadLetterEntry>();
        await foreach (var entry in _deadLetterStore.ReadAllAsync())
            entries.Add(entry);
        return entries;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Waits until <paramref name="ct"/> is cancelled, then fails the way SqlClient can: with an
    /// exception that is not an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task FailOnceCancelledAsync(CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult()))
        {
            await cancelled.Task;
        }
        throw new InvalidOperationException("A severe error occurred on the current command.");
    }
}
