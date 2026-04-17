using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public class SqlServerEventStoreHealthCheckTests
{
    [Fact]
    public async Task AddSqlServerEventStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .AddSqlServerEventStore("Server=.;Database=test;Trusted_Connection=true");

        var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("sqlserver-event-store");
        // Status may be Unhealthy (no DB) — we only verify registration, not connectivity
        report.Entries["sqlserver-event-store"].Status.Should().BeOneOf(
            HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);
    }
}
