namespace ZeroAlloc.EventSourcing;

/// <summary>Adapter-level type that holds serialized bytes and metadata, deferring deserialization until the payload is actually needed.</summary>
public readonly record struct RawEvent(
    StreamPosition Position,
    string EventType,
    ReadOnlyMemory<byte> Payload,
    EventMetadata Metadata);
