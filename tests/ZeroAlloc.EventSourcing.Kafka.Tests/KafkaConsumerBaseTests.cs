using Confluent.Kafka;
using FluentAssertions;
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
                            options ?? new StreamConsumerOptions(), deadLetterStore)
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
                            new StreamConsumerOptions(), null)
    {
        public override string ConsumerId => consumerId;
        protected override IReadOnlyList<int> GetAssignedPartitions() => [0, 1];
        protected override Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    }

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
