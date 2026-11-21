using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>Turns a stored dead-letter payload back into the event object.</summary>
internal static class DeadLetterPayload
{
    /// <summary>
    /// Resolves <paramref name="eventType"/> through <paramref name="registry"/> and deserializes
    /// <paramref name="payload"/> into that type.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The registry does not know <paramref name="eventType"/>. Skipping the row would hide it from
    /// monitoring and replay, so the read fails and names the row.
    /// </exception>
    public static object Deserialize(
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        string eventType,
        byte[] payload,
        string streamId,
        long position)
    {
        if (!registry.TryGetType(eventType, out var type) || type is null)
        {
            throw new InvalidOperationException(
                $"Dead-letter entry for stream '{streamId}' at position {position} has event type " +
                $"'{eventType}', which is not registered in the {nameof(IEventTypeRegistry)}. Register " +
                "the type, or remove the entry, to read the dead letters.");
        }

        return serializer.Deserialize(payload, type);
    }
}
