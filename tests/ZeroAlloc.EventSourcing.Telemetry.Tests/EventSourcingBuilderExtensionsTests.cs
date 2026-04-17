using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Telemetry;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        services.AddSingleton(Substitute.For<IEventStore>());
        return services;
    }

    [Fact]
    public void UseEventSourcingTelemetry_WrapsIEventStore_WithInstrumentedDecorator()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseEventSourcingTelemetry();
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<InstrumentedEventStore>();
    }

    [Fact]
    public void UseEventSourcingTelemetry_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseEventSourcingTelemetry().Should().BeSameAs(builder);
    }
}
