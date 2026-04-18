using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for Kafka stream consumers.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="KafkaConsumerOptions"/> and <see cref="KafkaStreamConsumer"/>.
    /// </summary>
    /// <param name="builder">The event sourcing builder.</param>
    /// <param name="options">Kafka consumer configuration.</param>
    /// <remarks>
    /// <see cref="ICheckpointStore"/>, <see cref="IEventTypeRegistry"/>, and
    /// <see cref="IEventSerializer"/> must be registered separately.
    /// <para>
    /// <see cref="IDeadLetterStore"/> is optional — if registered it will be injected automatically.
    /// </para>
    /// </remarks>
    public static EventSourcingBuilder UseKafka(
        this EventSourcingBuilder builder,
        KafkaConsumerOptions options)
    {
        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<KafkaStreamConsumer>();
        return builder;
    }

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
