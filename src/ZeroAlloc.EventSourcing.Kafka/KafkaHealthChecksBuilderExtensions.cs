using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// <see cref="IHealthChecksBuilder"/> extensions for Kafka event store health checks.
/// </summary>
public static class KafkaHealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="KafkaEventStoreHealthCheck"/> with the health check system.
    /// Fetches Kafka broker metadata as a lightweight connectivity check.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="bootstrapServers">Kafka bootstrap servers (e.g. <c>"localhost:9092"</c>).</param>
    /// <param name="name">Health check registration name. Defaults to <c>kafka-event-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    public static IHealthChecksBuilder AddKafkaEventStore(
        this IHealthChecksBuilder builder,
        string bootstrapServers,
        string name = "kafka-event-store",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new KafkaEventStoreHealthCheck(bootstrapServers),
            failureStatus,
            tags));
}
