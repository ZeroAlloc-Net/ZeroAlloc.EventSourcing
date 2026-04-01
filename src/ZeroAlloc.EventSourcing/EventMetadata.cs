namespace ZeroAlloc.EventSourcing;

/// <summary>Immutable metadata associated with a single event.</summary>
public readonly record struct EventMetadata(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? CorrelationId,
    Guid? CausationId)
{
    /// <summary>
    /// Creates a new <see cref="EventMetadata"/> instance with a version-7 UUID, the current UTC time,
    /// and optional correlation / causation identifiers.
    /// </summary>
    /// <param name="eventType">The logical event type name.</param>
    /// <param name="correlationId">Optional correlation identifier propagated across service boundaries.</param>
    /// <param name="causationId">Optional causation identifier that identifies the command or event that caused this event.</param>
    /// <returns>A fully populated <see cref="EventMetadata"/> value.</returns>
    public static EventMetadata New(string eventType, Guid? correlationId = null, Guid? causationId = null)
        => new(
            Guid.CreateVersion7(),
            eventType,
            DateTimeOffset.UtcNow,
            correlationId,
            causationId);
}
