using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>Turns a stored dead-letter payload back into the event object.</summary>
internal static class DeadLetterPayload
{
    /// <summary>The <see cref="ObsoleteAttribute"/> message of the constructors without a registry.</summary>
    public const string ObsoleteConstructor =
        "This constructor reads back the serialized payload as a byte[] in DeadLetterEntry.Envelope.Event. "
        + "Use the constructor that also takes an IEventTypeRegistry, which reads back the event object. "
        + "This constructor will be removed in the next major version.";

    /// <summary>The diagnostic id of <see cref="ObsoleteConstructor"/>.</summary>
    public const string ObsoleteConstructorId = "ZAES004";

    /// <summary>
    /// Resolves <paramref name="eventType"/> through <paramref name="registry"/> and deserializes
    /// <paramref name="payload"/> into that type.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The registry does not know <paramref name="eventType"/>. Skipping the row would hide it from
    /// monitoring and replay, so the read fails and names the row.
    /// </exception>
    /// <remarks>
    /// Without a registry, which only the obsolete constructors leave out, the payload is returned
    /// as it is stored.
    /// </remarks>
    public static object Deserialize(
        IEventSerializer serializer,
        IEventTypeRegistry? registry,
        string eventType,
        byte[] payload,
        string streamId,
        long position)
    {
        if (registry is null)
            return payload;

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
