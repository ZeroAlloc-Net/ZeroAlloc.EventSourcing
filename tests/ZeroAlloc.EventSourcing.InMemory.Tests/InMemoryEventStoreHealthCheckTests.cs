using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
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
    public void AddInMemoryEventStore_RegistersHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddInMemoryEventStore();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();
        healthCheckService.Should().NotBeNull();
    }
}
