namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Serializes and deserializes events to/from raw bytes.
/// Wrap ZeroAlloc.Serialisation.ISerializer{T} or any other serializer behind this interface.
/// </summary>
public interface IEventSerializer
{
    /// <summary>Serializes an event to a byte buffer.</summary>
    ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull;

    /// <summary>Deserializes a byte buffer to an event instance of the given type.</summary>
    object Deserialize(ReadOnlyMemory<byte> payload, Type eventType);
}
