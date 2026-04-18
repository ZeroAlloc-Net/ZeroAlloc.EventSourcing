using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public sealed class KafkaManualPartitionOptionsTests
{
    [Fact]
    public void Validate_ThrowsWhenBootstrapServersEmpty()
    {
        var o = new KafkaManualPartitionOptions { BootstrapServers = "", Topic = "orders", ConsumerId = "orders-consumer", Partitions = [0, 1] };
        var act = () => o.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*BootstrapServers*");
    }

    [Fact]
    public void Validate_ThrowsWhenTopicEmpty()
    {
        var o = new KafkaManualPartitionOptions { BootstrapServers = "localhost:9092", Topic = "", ConsumerId = "orders-consumer", Partitions = [0, 1] };
        var act = () => o.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Topic*");
    }

    [Fact]
    public void Validate_ThrowsWhenConsumerIdEmpty()
    {
        var o = new KafkaManualPartitionOptions { BootstrapServers = "localhost:9092", Topic = "orders", ConsumerId = "", Partitions = [0, 1] };
        var act = () => o.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ConsumerId*");
    }

    [Fact]
    public void Validate_ThrowsWhenPartitionsEmpty()
    {
        var o = new KafkaManualPartitionOptions { BootstrapServers = "localhost:9092", Topic = "orders", ConsumerId = "orders-consumer", Partitions = [] };
        var act = () => o.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Partitions*");
    }

    [Fact]
    public void Validate_PassesWithValidOptions()
    {
        var act = () => ValidOptions().Validate();
        act.Should().NotThrow();
    }

    private static KafkaManualPartitionOptions ValidOptions() => new()
    {
        BootstrapServers = "localhost:9092",
        Topic            = "orders",
        ConsumerId       = "orders-consumer",
        Partitions       = [0, 1],
    };
}

public sealed class KafkaConsumerGroupOptionsTests
{
    [Fact]
    public void Validate_ThrowsWhenGroupIdEmpty()
    {
        var o = new KafkaConsumerGroupOptions { BootstrapServers = "localhost:9092", Topic = "orders", GroupId = "", ConsumerId = "orders-consumer" };
        var act = () => o.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*GroupId*");
    }

    [Fact]
    public void Validate_PassesWithValidOptions()
    {
        var act = () => ValidOptions().Validate();
        act.Should().NotThrow();
    }

    private static KafkaConsumerGroupOptions ValidOptions() => new()
    {
        BootstrapServers = "localhost:9092",
        Topic            = "orders",
        GroupId          = "orders-group",
        ConsumerId       = "orders-consumer",
    };
}
