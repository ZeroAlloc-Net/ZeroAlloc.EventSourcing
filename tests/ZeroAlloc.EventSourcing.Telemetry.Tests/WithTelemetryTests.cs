using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

public class WithTelemetryTests
{
    private static IServiceCollection BaseServicesWithFakeRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAggregateRepository<FakeAggregate, OrderId>>());
        return services;
    }

    [Fact]
    public void WithTelemetry_DecoratesAggregateRepository_WithInstrumentedDecorator()
    {
        var services = BaseServicesWithFakeRepository();
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        repo.Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
    }

    [Fact]
    public void WithTelemetry_ReturnsBuilder_ForChaining()
    {
        var services = BaseServicesWithFakeRepository();
        var builder = services.AddEventSourcing();
        builder.WithTelemetry().Should().BeSameAs(builder);
    }

    [Fact]
    public void WithTelemetry_IsIdempotent_WhenCalledTwice()
    {
        var services = BaseServicesWithFakeRepository();
        services.AddEventSourcing()
                .WithTelemetry()
                .WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        // Idempotency: should still be a single instrumented decorator, not nested.
        repo.Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
    }

    [Fact]
    public void WithTelemetry_DoesNothing_WhenNoAggregateRepositoriesRegistered()
    {
        var services = new ServiceCollection();
        var builder = services.AddEventSourcing();
        var act = () => builder.WithTelemetry();
        act.Should().NotThrow();
    }

    [Fact]
    public void UseEventSourcingTelemetry_LegacyShim_DecoratesAggregateRepository()
    {
        var services = BaseServicesWithFakeRepository();
#pragma warning disable CS0618 // exercise the legacy shim
        services.AddEventSourcing().UseEventSourcingTelemetry();
#pragma warning restore CS0618

        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        repo.Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
    }
}
