using Confluent.Kafka;
using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

/// <summary>
/// Tests for shared KafkaConsumerBase behaviour (checkpoint key format, batch processing,
/// retry, dead-letter). Uses a minimal concrete subclass that does nothing in the abstract hooks.
/// </summary>
public sealed class KafkaConsumerBaseTests
{
    // ── minimal concrete subclass ──────────────────────────────────────────────

    private sealed class StubConsumer(
        IConsumer<string, byte[]> inner,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        string consumerId,
        IDeadLetterStore? deadLetterStore = null,
        StreamConsumerOptions? options = null)
        : KafkaConsumerBase(inner, checkpointStore, serializer, registry,
                            "test-topic", TimeSpan.FromMilliseconds(50),
                            options ?? new StreamConsumerOptions(), deadLetterStore,
                            ownsConsumer: false)
    {
        public override string ConsumerId => consumerId;
        protected override IReadOnlyList<int> GetAssignedPartitions() => [0];
        protected override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TwoPartitionStub(
        IConsumer<string, byte[]> inner,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        string consumerId)
        : KafkaConsumerBase(inner, checkpointStore, serializer, registry,
                            "test-topic", TimeSpan.FromMilliseconds(50),
                            new StreamConsumerOptions(), null, ownsConsumer: false)
    {
        public override string ConsumerId => consumerId;
        protected override IReadOnlyList<int> GetAssignedPartitions() => [0, 1];
        protected override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // The two stubs below bind to the obsolete injected-consumer constructor, once without and
    // once with the optional dead-letter store: the call shapes shipped before #379. They must
    // keep compiling, and keep leaving the injected consumer open.
#pragma warning disable ZAES002
    private sealed class LegacyShapeStub(
        IConsumer<string, byte[]> inner,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry)
        : KafkaConsumerBase(inner, checkpointStore, serializer, registry,
                            "test-topic", TimeSpan.FromMilliseconds(50),
                            new StreamConsumerOptions())
    {
        public override string ConsumerId => "legacy";
        protected override IReadOnlyList<int> GetAssignedPartitions() => [0];
        protected override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LegacyShapeDeadLetterStub(
        IConsumer<string, byte[]> inner,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        StreamConsumerOptions options,
        IDeadLetterStore deadLetterStore)
        : KafkaConsumerBase(inner, checkpointStore, serializer, registry,
                            "test-topic", TimeSpan.FromMilliseconds(50),
                            options, deadLetterStore)
    {
        public override string ConsumerId => "legacy";
        protected override IReadOnlyList<int> GetAssignedPartitions() => [0];
        protected override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    }
#pragma warning restore ZAES002

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ConsumeResult<string, byte[]> MakeMessage(int partition, long offset, string eventType = "OrderCreated")
    {
        var headers = new Headers();
        headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes(eventType));
        headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
        return new ConsumeResult<string, byte[]>
        {
            Topic     = "test-topic",
            Partition = new Partition(partition),
            Offset    = new Offset(offset),
            Message   = new Message<string, byte[]>
            {
                Key     = "stream-1",
                Value   = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { }),
                Headers = headers,
            },
        };
    }

    // ── checkpoint key ────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_WritesCheckpointWithPartitionQualifiedKey()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();
        var sut        = new StubConsumer(consumer, store, serializer, registry, "my-consumer");

        // Simulate having processed a message on partition 2
        sut.SimulateProcessed(2, new StreamPosition(10));

        await sut.CommitAsync();

        await store.Received(1).WriteAsync("my-consumer:p2", new StreamPosition(10), Arg.Any<CancellationToken>());
    }

    // ── GetPositionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPositionAsync_ReturnsNull_WhenNoCheckpointExists()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((StreamPosition?)null);

        var sut    = new StubConsumer(consumer, store, serializer, registry, "c1");
        var result = await sut.GetPositionAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPositionAsync_ReturnsMinAcrossPartitions()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync("c1:p0", Arg.Any<CancellationToken>()).Returns(new StreamPosition(5));
        store.ReadAsync("c1:p1", Arg.Any<CancellationToken>()).Returns(new StreamPosition(3));

        var sut    = new TwoPartitionStub(consumer, store, serializer, registry, "c1");
        var result = await sut.GetPositionAsync();

        result.Should().Be(new StreamPosition(3));
    }

    // ── retry ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_RetriesUpToMaxRetries_ThenThrowsOnFailFast()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(null); // end of batch
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        int callCount = 0;
        var options = new StreamConsumerOptions { MaxRetries = 2, ErrorStrategy = ErrorHandlingStrategy.FailFast };
        var sut = new StubConsumer(consumer, store, serializer, registry, "c", options: options);

        var act = () => sut.ConsumeAsync((_, _) =>
        {
            callCount++;
            throw new InvalidOperationException("handler failed");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        callCount.Should().Be(3); // 1 initial + 2 retries
    }

    [Fact]
    public async Task ConsumeAsync_SkipsEvent_WhenErrorStrategyIsSkip()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(MakeMessage(0, 2));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options    = new StreamConsumerOptions { MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.Skip };
        var sut        = new StubConsumer(consumer, store, serializer, registry, "c", options: options);
        var handledOffsets = new List<long>();

        await sut.ConsumeAsync((env, _) =>
        {
            handledOffsets.Add(env.Position.Value);
            if (env.Position.Value == 1) throw new InvalidOperationException("skip me");
            return Task.CompletedTask;
        });

        // Both events were passed to the handler (first failed+skipped, second succeeded)
        handledOffsets.Should().Equal(1L, 2L);
    }

    [Fact]
    public async Task ConsumeAsync_WritesToDeadLetterStore_WhenErrorStrategyIsDeadLetter()
    {
        var store        = Substitute.For<ICheckpointStore>();
        var consumer     = Substitute.For<IConsumer<string, byte[]>>();
        var serializer   = Substitute.For<IEventSerializer>();
        var registry     = Substitute.For<IEventTypeRegistry>();
        var deadLetter   = Substitute.For<IDeadLetterStore>();

        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions { MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.DeadLetter };
        var sut     = new StubConsumer(consumer, store, serializer, registry, "c",
                                       deadLetterStore: deadLetter, options: options);

        await sut.ConsumeAsync((_, _) => throw new InvalidOperationException("poison"));

        await deadLetter.Received(1).WriteAsync(
            Arg.Any<string>(), Arg.Any<EventEnvelope>(),
            Arg.Any<Exception>(), Arg.Any<CancellationToken>());
    }

    // ── shutdown is not a handler failure, see #422 ──────────────────────────

    [Theory]
    [InlineData(ErrorHandlingStrategy.Skip, CommitStrategy.AfterEvent)]
    [InlineData(ErrorHandlingStrategy.Skip, CommitStrategy.AfterBatch)]
    [InlineData(ErrorHandlingStrategy.DeadLetter, CommitStrategy.AfterEvent)]
    [InlineData(ErrorHandlingStrategy.DeadLetter, CommitStrategy.AfterBatch)]
    public async Task ConsumeAsync_HandlerFailingAfterCancellation_StopsWithoutApplyingTheErrorStrategy(
        ErrorHandlingStrategy strategy, CommitStrategy commit)
    {
        var (store, consumer, serializer, registry) = Substitutes();
        var deadLetter = Substitute.For<IDeadLetterStore>();
        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(MakeMessage(0, 2));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions { MaxRetries = 0, ErrorStrategy = strategy, CommitStrategy = commit };
        var sut = new StubConsumer(consumer, store, serializer, registry, "c", deadLetter, options);
        using var cts = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new List<long>();

        var consume = sut.ConsumeAsync(async (env, ct) =>
        {
            handled.Add(env.Position.Value);
            running.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult()))
                await cancelled.Task;
            throw new InvalidOperationException("A severe error occurred on the current command.");
        }, cts.Token);

        await running.Task;
        cts.Cancel();

        var thrown = await consume.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        handled.Should().Equal(1L);
        consumer.DidNotReceive().StoreOffset(Arg.Any<ConsumeResult<string, byte[]>>());
        await store.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>());
        await deadLetter.DidNotReceive().WriteAsync(
            Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_CancellationDuringRetryBackoff_StopsWithoutApplyingTheErrorStrategy()
    {
        var (store, consumer, serializer, registry) = Substitutes();
        var deadLetter = Substitute.For<IDeadLetterStore>();
        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 1,
            RetryPolicy = new ExponentialBackoffRetryPolicy(initialDelayMs: 600_000, maxDelayMs: 600_000),
            ErrorStrategy = ErrorHandlingStrategy.DeadLetter,
            CommitStrategy = CommitStrategy.AfterEvent,
        };
        var sut = new StubConsumer(consumer, store, serializer, registry, "c", deadLetter, options);
        using var cts = new CancellationTokenSource();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var consume = sut.ConsumeAsync((_, _) =>
        {
            calls++;
            failed.TrySetResult();
            throw new InvalidOperationException("transient");
        }, cts.Token);
        await failed.Task;
        cts.Cancel();

        await consume.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
        await store.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>());
        await deadLetter.DidNotReceive().WriteAsync(
            Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_HandlerFailingWhileRunning_WithALiveToken_IsDeadLetteredAndCheckpointed()
    {
        var (store, consumer, serializer, registry) = Substitutes();
        var deadLetter = Substitute.For<IDeadLetterStore>();
        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.DeadLetter, CommitStrategy = CommitStrategy.AfterEvent,
        };
        var sut = new StubConsumer(consumer, store, serializer, registry, "c", deadLetter, options);
        using var cts = new CancellationTokenSource();

        await sut.ConsumeAsync((_, _) => throw new InvalidOperationException("poison"), cts.Token);

        await deadLetter.Received(1).WriteAsync(
            Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<InvalidOperationException>(), Arg.Any<CancellationToken>());
        await store.Received(1).WriteAsync("c:p0", new StreamPosition(1), Arg.Any<CancellationToken>());
    }

    // ── a cancellation the consumer did not cause is a handler failure, see #426 ──

    /// <summary>
    /// A handler's own timeout surfaces as an <see cref="OperationCanceledException"/> while the
    /// consumer's token is live. It is retried, then handled by the error strategy, the same as
    /// <see cref="StreamConsumer"/> does, instead of stopping the consumer.
    /// </summary>
    [Theory]
    [InlineData(ErrorHandlingStrategy.Skip)]
    [InlineData(ErrorHandlingStrategy.DeadLetter)]
    public async Task ConsumeAsync_ForeignCancellation_WithALiveToken_IsRetriedThenHandledByTheErrorStrategy(
        ErrorHandlingStrategy strategy)
    {
        var (store, consumer, serializer, registry) = Substitutes();
        var deadLetter = Substitute.For<IDeadLetterStore>();
        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(MakeMessage(0, 2));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 2,
            RetryPolicy = new NoDelayRetryPolicy(),
            ErrorStrategy = strategy,
            CommitStrategy = CommitStrategy.AfterEvent,
        };
        var sut = new StubConsumer(consumer, store, serializer, registry, "c", deadLetter, options);
        using var cts = new CancellationTokenSource();
        var handled = new List<long>();

        await sut.ConsumeAsync((env, _) =>
        {
            handled.Add(env.Position.Value);
            if (env.Position.Value == 1) throw new OperationCanceledException("handler timeout");
            return Task.CompletedTask;
        }, cts.Token);

        handled.Should().Equal(1L, 1L, 1L, 2L);
        await store.Received(1).WriteAsync("c:p0", new StreamPosition(1), Arg.Any<CancellationToken>());
        await store.Received(1).WriteAsync("c:p0", new StreamPosition(2), Arg.Any<CancellationToken>());
        if (strategy == ErrorHandlingStrategy.DeadLetter)
        {
            await deadLetter.Received(1).WriteAsync(
                "c", Arg.Is<EventEnvelope>(e => e.Position.Value == 1),
                Arg.Any<OperationCanceledException>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await deadLetter.DidNotReceive().WriteAsync(
                Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ConsumeAsync_ForeignCancellation_WithALiveToken_IsRetriedThenRethrownOnFailFast()
    {
        var (store, consumer, serializer, registry) = Substitutes();
        var messages = new Queue<ConsumeResult<string, byte[]>?>();
        messages.Enqueue(MakeMessage(0, 1));
        messages.Enqueue(null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 2, RetryPolicy = new NoDelayRetryPolicy(), ErrorStrategy = ErrorHandlingStrategy.FailFast,
        };
        var sut = new StubConsumer(consumer, store, serializer, registry, "c", options: options);
        using var cts = new CancellationTokenSource();
        var timeout = new OperationCanceledException("handler timeout");
        var calls = 0;

        var consume = sut.ConsumeAsync((_, _) =>
        {
            calls++;
            throw timeout;
        }, cts.Token);

        var thrown = await consume.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(timeout);
        calls.Should().Be(3);
        cts.IsCancellationRequested.Should().BeFalse();
        await store.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>());
    }

    private sealed class NoDelayRetryPolicy : IRetryPolicy
    {
        public TimeSpan GetDelay(int attemptNumber) => TimeSpan.Zero;
    }

    private static (ICheckpointStore Store, IConsumer<string, byte[]> Consumer, IEventSerializer Serializer,
        IEventTypeRegistry Registry) Substitutes()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();
        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());
        return (store, consumer, serializer, registry);
    }

    // ── obsolete constructor shape ────────────────────────────────────────────

    [Fact]
    public void ObsoleteConstructor_DoesNotDisposeInjectedConsumer()
    {
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var sut = new LegacyShapeStub(consumer, Substitute.For<ICheckpointStore>(),
                                      Substitute.For<IEventSerializer>(),
                                      Substitute.For<IEventTypeRegistry>());

        sut.Dispose();

        consumer.DidNotReceive().Close();
        consumer.DidNotReceive().Dispose();
    }

    [Fact]
    public void OwnsConsumerFalse_DoesNotDisposeInjectedConsumer()
    {
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var sut = new StubConsumer(consumer, Substitute.For<ICheckpointStore>(),
                                   Substitute.For<IEventSerializer>(),
                                   Substitute.For<IEventTypeRegistry>(), "c");

        sut.Dispose();

        consumer.DidNotReceive().Close();
        consumer.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task ObsoleteConstructor_StillRoutesToDeadLetterStore()
    {
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();
        var deadLetter = Substitute.For<IDeadLetterStore>();

        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>([MakeMessage(0, 1), null]);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions { MaxRetries = 0, ErrorStrategy = ErrorHandlingStrategy.DeadLetter };
        var sut = new LegacyShapeDeadLetterStub(consumer, Substitute.For<ICheckpointStore>(),
                                                serializer, registry, options, deadLetter);

        await sut.ConsumeAsync((_, _) => throw new InvalidOperationException("poison"));

        await deadLetter.Received(1).WriteAsync(
            Arg.Any<string>(), Arg.Any<EventEnvelope>(),
            Arg.Any<Exception>(), Arg.Any<CancellationToken>());
        consumer.DidNotReceive().Dispose();
    }

    // ── commit strategies ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_WritesCheckpointAfterBatch_WhenCommitStrategyIsAfterBatch()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>(
        [MakeMessage(0, 1), MakeMessage(0, 2), null]);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions { CommitStrategy = CommitStrategy.AfterBatch };
        var sut     = new StubConsumer(consumer, store, serializer, registry, "c", options: options);

        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        // After the batch, WriteAsync should be called for partition 0 with the last offset (2)
        await store.Received(1).WriteAsync("c:p0", new StreamPosition(2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_WritesCheckpointAfterEachEvent_WhenCommitStrategyIsAfterEvent()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        registry.TryGetType("OrderCreated", out _).Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>()).Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>(
        [MakeMessage(0, 1), MakeMessage(0, 2), null]);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var options = new StreamConsumerOptions { CommitStrategy = CommitStrategy.AfterEvent };
        var sut     = new StubConsumer(consumer, store, serializer, registry, "c", options: options);

        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        // WriteAsync called once per event (2 times total)
        await store.Received(2).WriteAsync(Arg.Any<string>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>());
        await store.Received(1).WriteAsync("c:p0", new StreamPosition(1), Arg.Any<CancellationToken>());
        await store.Received(1).WriteAsync("c:p0", new StreamPosition(2), Arg.Any<CancellationToken>());
    }
}
