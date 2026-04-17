using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class KafkaEventStoreHealthCheckTests
{
    [Fact]
    public async Task AddKafkaEventStore_RegistersHealthCheckUnderExpectedName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .AddKafkaEventStore("localhost:9092");

        var provider = services.BuildServiceProvider();
        var report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        report.Entries.Should().ContainKey("kafka-event-store");
        // Status may be Unhealthy (no broker) — we only verify registration, not connectivity
        report.Entries["kafka-event-store"].Status.Should().BeOneOf(
            HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy);
    }
}
