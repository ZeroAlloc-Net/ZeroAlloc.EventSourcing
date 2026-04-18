using Confluent.Kafka;
using FluentAssertions;
using NSubstitute;
using Xunit;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public sealed class KafkaManualPartitionConsumerTests
{
    private static KafkaManualPartitionOptions Options(int[]? partitions = null) => new()
    {
        BootstrapServers = "localhost:9092",
        Topic            = "orders",
        ConsumerId       = "orders-consumer",
        Partitions       = partitions ?? [0, 1],
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
    public async Task ConsumeAsync_AssignsConfiguredPartitionsWithCheckpointOffsets()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync("orders-consumer:p0", Arg.Any<CancellationToken>())
             .Returns(new StreamPosition(5));
        store.ReadAsync("orders-consumer:p1", Arg.Any<CancellationToken>())
             .Returns(new StreamPosition(10));
        consumer.Consume(Arg.Any<TimeSpan>()).Returns((ConsumeResult<string, byte[]>?)null);

        var sut = new KafkaManualPartitionConsumer(
            Options([0, 1]), store, serializer, registry, consumer);

        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        consumer.Received(1).Assign(Arg.Is<IEnumerable<TopicPartitionOffset>>(tpos =>
            tpos.Any(t => t.Partition.Value == 0 && t.Offset.Value == 5) &&
            tpos.Any(t => t.Partition.Value == 1 && t.Offset.Value == 10)));
    }

    [Fact]
    public async Task ConsumeAsync_SeeksToBeginning_WhenNoCheckpointExists()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns((StreamPosition?)null);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns((ConsumeResult<string, byte[]>?)null);

        var sut = new KafkaManualPartitionConsumer(
            Options([0]), store, serializer, registry, consumer);

        await sut.ConsumeAsync((_, _) => Task.CompletedTask);

        consumer.Received(1).Assign(Arg.Is<IEnumerable<TopicPartitionOffset>>(tpos =>
            tpos.Any(t => t.Partition.Value == 0 && t.Offset == Offset.Beginning)));
    }

    [Fact]
    public async Task ConsumeAsync_ProcessesMessagesFromMultiplePartitions()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns((StreamPosition?)null);
        registry.TryGetType("OrderCreated", out _)
                .Returns(x => { x[1] = typeof(object); return true; });
        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>())
                  .Returns(new object());

        var messages = new Queue<ConsumeResult<string, byte[]>?>(
        [
            MakeMessage(0, 1),
            MakeMessage(1, 1),
            null,
        ]);
        consumer.Consume(Arg.Any<TimeSpan>()).Returns(_ => messages.Count > 0 ? messages.Dequeue() : null);

        var sut      = new KafkaManualPartitionConsumer(Options([0, 1]), store, serializer, registry, consumer);
        var received = new List<(int partition, long offset)>();

        await sut.ConsumeAsync((env, _) =>
        {
            received.Add(((int)env.Position.Value, (long)env.Position.Value));
            return Task.CompletedTask;
        });

        received.Should().HaveCount(2);
    }

    [Fact]
    public void ConsumerId_ReturnsConfiguredValue()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        var sut = new KafkaManualPartitionConsumer(Options(), store, serializer, registry, consumer);
        sut.ConsumerId.Should().Be("orders-consumer");
    }

    [Fact]
    public async Task GetPositionAsync_ReturnsMinAcrossPartitions()
    {
        var store      = Substitute.For<ICheckpointStore>();
        var consumer   = Substitute.For<IConsumer<string, byte[]>>();
        var serializer = Substitute.For<IEventSerializer>();
        var registry   = Substitute.For<IEventTypeRegistry>();

        store.ReadAsync("orders-consumer:p0", Arg.Any<CancellationToken>()).Returns(new StreamPosition(7));
        store.ReadAsync("orders-consumer:p1", Arg.Any<CancellationToken>()).Returns(new StreamPosition(3));

        var sut    = new KafkaManualPartitionConsumer(Options([0, 1]), store, serializer, registry, consumer);
        var result = await sut.GetPositionAsync();

        result.Should().Be(new StreamPosition(3));
    }
}
