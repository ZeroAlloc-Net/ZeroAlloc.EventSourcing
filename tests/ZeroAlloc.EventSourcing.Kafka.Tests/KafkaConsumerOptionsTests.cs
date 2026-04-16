using FluentAssertions;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class KafkaConsumerOptionsTests
{
    [Fact]
    public void Partitions_DefaultsToSinglePartitionZero()
    {
        // Arrange & Act
        var options = new KafkaConsumerOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "test-topic",
            GroupId = "test-group"
        };

        // Assert
        options.Partitions.Should().Equal([0]);
    }

    [Fact]
    public void Partitions_AcceptsMultiplePartitions()
    {
        // Arrange & Act
        var options = new KafkaConsumerOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "test-topic",
            GroupId = "test-group",
            Partitions = [0, 1, 2]
        };

        // Assert
        options.Partitions.Should().Equal([0, 1, 2]);
    }
}
