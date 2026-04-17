using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// <see cref="IEventSerializer"/> backed by ZeroAlloc.Serialisation's source-generated
/// <see cref="ISerializerDispatcher"/>. Zero-reflection, AOT-safe.
/// </summary>
public sealed class ZeroAllocEventSerializer : IEventSerializer
{
    private readonly ISerializerDispatcher _dispatcher;

    /// <summary>Initializes a new instance backed by the given <paramref name="dispatcher"/>.</summary>
    /// <param name="dispatcher">The serializer dispatcher used to serialize and deserialize events.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dispatcher"/> is <see langword="null"/>.</exception>
    public ZeroAllocEventSerializer(ISerializerDispatcher dispatcher)
        => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => _dispatcher.Serialize(@event, typeof(TEvent));

    /// <inheritdoc/>
    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => _dispatcher.Deserialize(payload, eventType)
           ?? throw new InvalidOperationException(
               $"Deserialization of {eventType.FullName} returned null.");
}
