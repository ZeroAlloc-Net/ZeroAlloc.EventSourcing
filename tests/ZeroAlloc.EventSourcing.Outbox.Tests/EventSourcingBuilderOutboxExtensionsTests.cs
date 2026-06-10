using System;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class EventSourcingBuilderOutboxExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INotificationDispatcher, RecordingDispatcher>();
        services.AddSingleton<IEventTypeRegistry, OutboxDispatcherTests.OutboxTestTypeRegistry>();
        services.AddSingleton<IEventSerializer, OutboxDispatcherTests.OutboxJsonSerializer>();
        return services;
    }

    [Fact]
    public async Task AddOutbox_registers_dispatcher_as_hosted_service()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseInMemoryCheckpointStore()
                .UseInMemoryEventStore()
                .AddOutbox(opts => opts.ConsumerId = "test-app");

        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is OutboxDispatcher);
    }

    [Fact]
    public async Task AddOutbox_applies_configure_action_to_options()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseInMemoryCheckpointStore()
                .UseInMemoryEventStore()
                .AddOutbox(opts =>
                {
                    opts.ConsumerId = "custom";
                    opts.BatchSize = 50;
                    opts.Exclude<TestEventA>();
                });

        await using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<OutboxOptions>();
        opts.ConsumerId.Should().Be("custom");
        opts.BatchSize.Should().Be(50);
        opts.ExcludedTypes.Should().ContainSingle().Which.Should().Be(typeof(TestEventA));
    }

    [Fact]
    public async Task AddOutbox_without_configure_action_uses_defaults()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseInMemoryCheckpointStore()
                .UseInMemoryEventStore()
                .AddOutbox();

        await using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<OutboxOptions>();
        opts.ConsumerId.Should().Be("outbox");
        opts.BatchSize.Should().Be(100);
    }

    [Fact]
    public void AddOutbox_throws_on_null_builder()
    {
        EventSourcingBuilder? builder = null;
        var act = () => builder!.AddOutbox();

        act.Should().Throw<ArgumentNullException>();
    }
}
