namespace ZeroAlloc.EventSourcing;

/// <summary>Adapter-level type that holds serialized bytes and metadata, deferring deserialization until the payload is actually needed.</summary>
/// <remarks>
/// Note: <c>record struct</c> equality compares <see cref="RawEvent.Payload"/> by its backing object reference,
/// not byte-for-byte content. Do not rely on structural equality of <see cref="RawEvent"/> in tests
/// or production logic that compares payloads.
/// </remarks>
public readonly record struct RawEvent(
    StreamPosition Position,
    string EventType,
    ReadOnlyMemory<byte> Payload,
    EventMetadata Metadata);
