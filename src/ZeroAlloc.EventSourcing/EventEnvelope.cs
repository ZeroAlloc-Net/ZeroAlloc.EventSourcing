namespace ZeroAlloc.EventSourcing;

/// <summary>Typed envelope that pairs a strongly typed event with its stream location and metadata.</summary>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public readonly record struct EventEnvelope<TEvent>(
    StreamId StreamId,
    StreamPosition Position,
    TEvent Event,
    EventMetadata Metadata);

/// <summary>
/// Untyped event envelope returned by <c>IEventStore.ReadAsync</c>. The <see cref="Event"/>
/// property is typed as <see cref="object"/>; value-type events will be boxed when stored here.
/// Prefer <see cref="EventEnvelope{TEvent}"/> on typed hot paths.
/// </summary>
public readonly record struct EventEnvelope(
    StreamId StreamId,
    StreamPosition Position,
    object Event,
    EventMetadata Metadata);
