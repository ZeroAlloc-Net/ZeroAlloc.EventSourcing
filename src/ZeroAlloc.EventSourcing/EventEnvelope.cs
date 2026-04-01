namespace ZeroAlloc.EventSourcing;

/// <summary>Typed envelope that pairs a strongly typed event with its stream location and metadata.</summary>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public readonly record struct EventEnvelope<TEvent>(
    StreamId StreamId,
    StreamPosition Position,
    TEvent Event,
    EventMetadata Metadata);

/// <summary>Untyped envelope used internally for heterogeneous reads where the concrete event type is not statically known.</summary>
public readonly record struct EventEnvelope(
    StreamId StreamId,
    StreamPosition Position,
    object Event,
    EventMetadata Metadata);
