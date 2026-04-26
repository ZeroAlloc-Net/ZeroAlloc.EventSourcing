using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

public class WithTelemetryTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventTypeRegistry>());
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        return services;
    }

    [Fact]
    public void WithTelemetry_RegistersInstrumentedEventStore()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseInMemoryEventStore()
                .WithTelemetry();

        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IEventStore>();
        store.GetType().Name.Should().Contain("Instrumented");
    }

    [Fact]
    public void UseEventSourcingTelemetry_LegacyShim_StillWraps()
    {
        var services = BaseServices();
#pragma warning disable CS0618 // exercise the legacy shim
        services.AddEventSourcing()
                .UseInMemoryEventStore()
                .UseEventSourcingTelemetry();
#pragma warning restore CS0618

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IEventStore>().GetType().Name.Should().Contain("Instrumented");
    }
}
