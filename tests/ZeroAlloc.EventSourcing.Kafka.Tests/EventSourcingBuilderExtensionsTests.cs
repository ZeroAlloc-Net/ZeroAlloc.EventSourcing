using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ICheckpointStore>());
        services.AddSingleton(Substitute.For<IEventSerializer>());
        return services;
    }

    [Fact]
    public void UseKafkaManualPartitions_RegistersKafkaManualPartitionConsumer()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseKafkaManualPartitions(new KafkaManualPartitionOptions
                {
                    BootstrapServers = "localhost:9092",
                    Topic            = "orders",
                    ConsumerId       = "orders-consumer",
                    Partitions       = [0],
                });

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(KafkaManualPartitionConsumer));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void UseKafkaManualPartitions_RegistersOptions()
    {
        var services = BaseServices();
        var options = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            ConsumerId       = "orders-consumer",
            Partitions       = [0],
        };
        services.AddEventSourcing().UseKafkaManualPartitions(options);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaManualPartitionOptions>().Should().BeSameAs(options);
    }

    [Fact]
    public void UseKafkaManualPartitions_DoesNotOverwriteUserOptions()
    {
        var services = BaseServices();
        var userOptions = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            ConsumerId       = "user-consumer",
            Partitions       = [0],
        };
        services.AddSingleton(userOptions);

        var otherOptions = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            ConsumerId       = "other-consumer",
            Partitions       = [1],
        };
        services.AddEventSourcing().UseKafkaManualPartitions(otherOptions);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaManualPartitionOptions>().Should().BeSameAs(userOptions);
    }

    [Fact]
    public void UseKafkaManualPartitions_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UseKafkaManualPartitions(new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            ConsumerId       = "orders-consumer",
            Partitions       = [0],
        });

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void UseKafkaConsumerGroup_RegistersKafkaConsumerGroupConsumer()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseKafkaConsumerGroup(new KafkaConsumerGroupOptions
                {
                    BootstrapServers = "localhost:9092",
                    Topic            = "orders",
                    GroupId          = "orders-group",
                    ConsumerId       = "orders-consumer",
                });

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(KafkaConsumerGroupConsumer));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void UseKafkaConsumerGroup_RegistersOptions()
    {
        var services = BaseServices();
        var options = new KafkaConsumerGroupOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            GroupId          = "orders-group",
            ConsumerId       = "orders-consumer",
        };
        services.AddEventSourcing().UseKafkaConsumerGroup(options);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaConsumerGroupOptions>().Should().BeSameAs(options);
    }

    [Fact]
    public void UseKafkaConsumerGroup_DoesNotOverwriteUserOptions()
    {
        var services = BaseServices();
        var userOptions = new KafkaConsumerGroupOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            GroupId          = "orders-group",
            ConsumerId       = "user-consumer",
        };
        services.AddSingleton(userOptions);

        var otherOptions = new KafkaConsumerGroupOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            GroupId          = "orders-group",
            ConsumerId       = "other-consumer",
        };
        services.AddEventSourcing().UseKafkaConsumerGroup(otherOptions);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaConsumerGroupOptions>().Should().BeSameAs(userOptions);
    }

    [Fact]
    public void UseKafkaConsumerGroup_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UseKafkaConsumerGroup(new KafkaConsumerGroupOptions
        {
            BootstrapServers = "localhost:9092",
            Topic            = "orders",
            GroupId          = "orders-group",
            ConsumerId       = "orders-consumer",
        });

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void UseKafkaManualPartitions_ThrowsWhenOptionsIsNull()
    {
        var builder = new ServiceCollection().AddEventSourcing();
        var act = () => builder.UseKafkaManualPartitions(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UseKafkaConsumerGroup_ThrowsWhenOptionsIsNull()
    {
        var builder = new ServiceCollection().AddEventSourcing();
        var act = () => builder.UseKafkaConsumerGroup(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
