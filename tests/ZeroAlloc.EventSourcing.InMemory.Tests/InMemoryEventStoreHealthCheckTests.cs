using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public class InMemoryEventStoreHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy()
    {
        var check = new InMemoryEventStoreHealthCheck();
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", check, null, null)
        };

        var result = await check.CheckHealthAsync(context, CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AddInMemoryEventStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddInMemoryEventStore();

        var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("inmemory-event-store");
        report.Entries["inmemory-event-store"].Status.Should().Be(HealthStatus.Healthy);
    }
}
