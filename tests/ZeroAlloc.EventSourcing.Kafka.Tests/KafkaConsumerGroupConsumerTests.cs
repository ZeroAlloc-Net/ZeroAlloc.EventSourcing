using Confluent.Kafka;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public sealed class KafkaConsumerGroupConsumerTests
{
    private static KafkaConsumerGroupOptions Options() => new()
    {
        BootstrapServers = "localhost:9092",
        Topic            = "orders",
        GroupId          = "orders-group",
        ConsumerId       = "orders-consumer",
    };

    private static ConsumeResult<string, byte[]> MakeMessage(int partition, long offset)
    {
        var headers = new Headers();
        headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes("OrderCreated"));
        headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
        return new ConsumeResult<string, byte[]>
        {
            Topic     = "orders",
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

    [Fact]
    public async Task ConsumeAsync_CallsSubscribeNotAssign()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        consumer.Consume(Arg.Any<TimeSpan>()).Returns((ConsumeResult<string, byte[]>?)null);

        var sut = new KafkaConsumerGroupConsumer(Options(), store, serializer, registry, consumer);
        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        consumer.Received(1).Subscribe("orders");
        consumer.DidNotReceiveWithAnyArgs().Assign(default(IEnumerable<TopicPartitionOffset>)!);
    }

    [Fact]
    public async Task PollLoop_SeeksToCheckpoint_AfterPartitionsAssigned()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync("orders-consumer:p0", Arg.Any<CancellationToken>())
             .Returns(new StreamPosition(42));

        // Simulate Kafka assigning partition 0 when Subscribe() is called (before polling starts).
        // This means the pending seek is queued before DrainPendingSeeksAsync runs.
        KafkaConsumerGroupConsumer? sut = null;
        consumer.When(c => c.Subscribe("orders"))
                .Do(_ => sut!.SimulatePartitionsAssigned([new TopicPartition("orders", new Partition(0))]));

        consumer.Consume(Arg.Any<TimeSpan>()).Returns((ConsumeResult<string, byte[]>?)null);

        sut = new KafkaConsumerGroupConsumer(Options(), store, serializer, registry, consumer);
        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        consumer.Received(1).Seek(Arg.Is<TopicPartitionOffset>(
            tpo => tpo.Partition.Value == 0 && tpo.Offset.Value == 42));
    }

    [Fact]
    public async Task PollLoop_SeeksToBeginning_WhenNoCheckpointForAssignedPartition()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns((StreamPosition?)null);

        KafkaConsumerGroupConsumer? sut = null;
        consumer.When(c => c.Subscribe("orders"))
                .Do(_ => sut!.SimulatePartitionsAssigned([new TopicPartition("orders", new Partition(0))]));

        consumer.Consume(Arg.Any<TimeSpan>()).Returns((ConsumeResult<string, byte[]>?)null);

        sut = new KafkaConsumerGroupConsumer(Options(), store, serializer, registry, consumer);
        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        consumer.Received(1).Seek(Arg.Is<TopicPartitionOffset>(
            tpo => tpo.Partition.Value == 0 && tpo.Offset == Offset.Beginning));
    }

    [Fact]
    public async Task RevokeHandler_CommitsAndRemovesRevokedPartitions()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns((StreamPosition?)null);

        // Assign partition 0 at subscribe time, then revoke it on the first Consume() call.
        KafkaConsumerGroupConsumer? sut = null;
        consumer.When(c => c.Subscribe("orders"))
                .Do(_ => sut!.SimulatePartitionsAssigned([new TopicPartition("orders", new Partition(0))]));

        int consumeCallCount = 0;
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ =>
        {
            // On the first actual poll (after the seek has been processed), trigger a revoke.
            if (++consumeCallCount == 1)
                sut!.SimulatePartitionsRevoked([new TopicPartition("orders", new Partition(0))]);
            return (ConsumeResult<string, byte[]>?)null;
        });

        sut = new KafkaConsumerGroupConsumer(Options(), store, serializer, registry, consumer);
        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        // Sync Commit() must have been called during revoke.
        consumer.Received().Commit();
    }

    [Fact]
    public async Task ConsumeAsync_ParksWithoutProcessing_WhenZeroPartitionsAssigned()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        consumer.Consume(Arg.Any<TimeSpan>()).Returns((ConsumeResult<string, byte[]>?)null);

        var sut      = new KafkaConsumerGroupConsumer(Options(), store, serializer, registry, consumer);
        var received = new List<EventEnvelope>();

        await sut.ConsumeAsync((env, _) => { received.Add(env); return Task.CompletedTask; });

        received.Should().BeEmpty();
    }
}
