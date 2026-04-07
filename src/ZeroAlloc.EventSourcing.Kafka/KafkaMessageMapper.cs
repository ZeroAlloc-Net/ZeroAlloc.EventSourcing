using System.Text;
using Confluent.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Maps Kafka messages to EventEnvelope for processing.
/// Performs impedance matching between Confluent.Kafka types and domain types.
/// </summary>
internal static class KafkaMessageMapper
{
    private const string EventTypeHeader = "event-type";
    private const string EventIdHeader = "event-id";
    private const string OccurredAtHeader = "occurred-at";
    private const string CorrelationIdHeader = "correlation-id";
    private const string CausationIdHeader = "causation-id";

    /// <summary>
    /// Converts a Kafka ConsumeResult to an EventEnvelope.
    /// </summary>
    /// <param name="result">The Kafka consume result (message + metadata).</param>
    /// <param name="serializer">Serializer for deserializing message payload.</param>
    /// <param name="registry">Type registry for resolving event types.</param>
    /// <returns>An EventEnvelope with the deserialized event and metadata.</returns>
    /// <exception cref="InvalidOperationException">If event-type header is missing or type is not registered.</exception>
    public static EventEnvelope ToEnvelope(
        ConsumeResult<string, byte[]> result,
        IEventSerializer serializer,
        IEventTypeRegistry registry)
    {
        // Extract StreamId from message key, fall back to topic if key is null/empty
        var streamId = new StreamId(result.Message.Key ?? result.Topic);

        // Extract StreamPosition from Kafka offset
        var position = ToStreamPosition(result.Offset);

        // Extract event type from header
        var eventType = ReadHeader(result.Message.Headers, EventTypeHeader)
            ?? throw new InvalidOperationException(
                $"Missing '{EventTypeHeader}' header on message at {result.Topic}[{result.Partition}] offset {result.Offset.Value}. " +
                $"Header is required for event type resolution.");

        // Resolve the type using the registry
        if (!registry.TryGetType(eventType, out var type) || type == null)
            throw new InvalidOperationException(
                $"Unknown event type '{eventType}' at {result.Topic}[{result.Partition}] offset {result.Offset.Value}. " +
                $"Type is not registered in IEventTypeRegistry.");

        // Deserialize the payload
        var @event = serializer.Deserialize(result.Message.Value.AsMemory(), type);

        // Extract metadata from headers
        var eventId = TryReadGuid(result.Message.Headers, EventIdHeader) ?? Guid.NewGuid();
        var occurredAt = TryReadDateTimeOffset(result.Message.Headers, OccurredAtHeader)
            ?? DateTimeOffset.UtcNow;
        var correlationId = TryReadGuid(result.Message.Headers, CorrelationIdHeader);
        var causationId = TryReadGuid(result.Message.Headers, CausationIdHeader);

        var metadata = new EventMetadata(eventId, eventType, occurredAt, correlationId, causationId);

        return new EventEnvelope(streamId, position, @event, metadata);
    }

    /// <summary>
    /// Converts a Kafka Offset to StreamPosition.
    /// </summary>
    public static StreamPosition ToStreamPosition(Offset offset)
        => new(offset.Value);

    /// <summary>
    /// Converts a StreamPosition to Kafka Offset.
    /// </summary>
    public static Offset ToOffset(StreamPosition position)
        => new(position.Value);

    /// <summary>
    /// Reads a string header value from message headers.
    /// Returns null if the header is not present.
    /// </summary>
    private static string? ReadHeader(Headers headers, string headerName)
    {
        if (headers == null)
            return null;

        var header = headers.FirstOrDefault(h => string.Equals(h.Key, headerName));
        if (header == null)
            return null;

        return Encoding.UTF8.GetString(header.GetValueBytes().AsSpan());
    }

    /// <summary>
    /// Tries to read a Guid from a message header.
    /// Returns null if the header is not present or cannot be parsed.
    /// </summary>
    private static Guid? TryReadGuid(Headers headers, string headerName)
    {
        var value = ReadHeader(headers, headerName);
        if (string.IsNullOrEmpty(value))
            return null;

        if (Guid.TryParse(value, out var guid))
            return guid;

        return null;
    }

    /// <summary>
    /// Tries to read a DateTimeOffset from a message header.
    /// Expects ISO-8601 format.
    /// Returns null if the header is not present or cannot be parsed.
    /// </summary>
    private static DateTimeOffset? TryReadDateTimeOffset(Headers headers, string headerName)
    {
        var value = ReadHeader(headers, headerName);
        if (string.IsNullOrEmpty(value))
            return null;

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dateTime))
            return dateTime;

        return null;
    }
}
