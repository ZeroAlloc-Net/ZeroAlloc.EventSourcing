using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
}
