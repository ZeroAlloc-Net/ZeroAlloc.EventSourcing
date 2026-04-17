using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private static KafkaConsumerOptions FakeOptions() => new()
    {
        BootstrapServers = "localhost:9092",
        Topic = "events",
        GroupId = "test-group",
    };

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ICheckpointStore>());
        services.AddSingleton(Substitute.For<IEventSerializer>());
        return services;
    }

    [Fact]
    public void UseKafka_RegistersKafkaConsumerOptions()
    {
        var services = BaseServices();
        var options = FakeOptions();
        services.AddEventSourcing().UseKafka(options);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaConsumerOptions>().Should().BeSameAs(options);
    }

    [Fact]
    public void UseKafka_RegistersKafkaStreamConsumer()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseKafka(FakeOptions());

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaStreamConsumer>().Should().BeOfType<KafkaStreamConsumer>();
    }

    [Fact]
    public void UseKafka_DoesNotOverwriteUserOptions()
    {
        var services = BaseServices();
        var userOptions = FakeOptions();
        services.AddSingleton(userOptions);

        var otherOptions = FakeOptions();
        services.AddEventSourcing().UseKafka(otherOptions);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<KafkaConsumerOptions>().Should().BeSameAs(userOptions);
    }

    [Fact]
    public void UseKafka_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UseKafka(FakeOptions());

        result.Should().BeSameAs(builder);
    }
}
