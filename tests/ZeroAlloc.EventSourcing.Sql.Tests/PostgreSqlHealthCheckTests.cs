using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public class PostgreSqlHealthCheckTests
{
    [Fact]
    public async Task AddPostgreSqlEventStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddPostgreSqlEventStore("Host=localhost;Database=test");

        var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("postgresql-event-store");
        // Status may be Unhealthy (no DB) — we only verify registration, not connectivity
        report.Entries["postgresql-event-store"].Status.Should().BeOneOf(
            HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task AddPostgreSqlCheckpointStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks().AddPostgreSqlCheckpointStore("Host=localhost;Database=test");

        var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("postgresql-checkpoint-store");
        report.Entries["postgresql-checkpoint-store"].Status.Should().BeOneOf(
            HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);
    }
}
