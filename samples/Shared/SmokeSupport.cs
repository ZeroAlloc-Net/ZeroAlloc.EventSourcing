// samples/Shared/SmokeSupport.cs
// Compiled into the adapter AOT smokes that need a live service: the event and state types,
// a hand-rolled serializer and registry, and the helpers that read the service address and
// fail the smoke. Hand-rolled so the publish stays free of trim warnings from a serializer.
using System.Buffers.Binary;

namespace ZeroAlloc.EventSourcing.AotSmoke.Shared;

/// <summary>Event written and read back by the smokes.</summary>
public readonly record struct Ping(long Sequence);

/// <summary>Snapshot state written and read back by the smokes.</summary>
public readonly record struct PingTotal(long Total);

/// <summary>Thrown when a smoke check fails; the message says which.</summary>
public sealed class SmokeFailure(string message) : Exception(message);

/// <summary>Helpers shared by the adapter smokes.</summary>
public static class Smoke
{
    /// <summary>
    /// Reads the address of the service a smoke runs against. CI sets it in the aot-smoke job,
    /// which starts the services; a smoke never passes by skipping, so a missing value fails it.
    /// </summary>
    public static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new SmokeFailure($"{name} is not set. Point it at a running service to run this smoke.");
        return value;
    }

    /// <summary>Fails the smoke with <paramref name="message"/> when <paramref name="condition"/> is false.</summary>
    public static void Check(bool condition, string message)
    {
        if (!condition)
            throw new SmokeFailure(message);
    }

    /// <summary>A stream or key name no earlier run used, so a smoke can rerun against the same service.</summary>
    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

/// <summary>Maps the event and state names used by the smokes to their CLR types.</summary>
public sealed class PingRegistry : IEventTypeRegistry
{
    /// <inheritdoc/>
    public bool TryGetType(string eventType, out Type? type)
    {
        type = eventType switch
        {
            nameof(Ping) => typeof(Ping),
            nameof(PingTotal) => typeof(PingTotal),
            _ => null,
        };
        return type is not null;
    }

    /// <inheritdoc/>
    public string GetTypeName(Type type) => type.Name;
}

/// <summary>Binary serializer for <see cref="Ping"/> and <see cref="PingTotal"/>: one little-endian long.</summary>
public sealed class PingSerializer : IEventSerializer
{
    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        var value = @event switch
        {
            Ping p => p.Sequence,
            PingTotal t => t.Total,
            _ => throw new NotSupportedException($"Unsupported event type {@event.GetType().FullName}"),
        };
        var buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return buffer;
    }

    /// <inheritdoc/>
    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
        if (eventType == typeof(Ping))
            return new Ping(value);
        if (eventType == typeof(PingTotal))
            return new PingTotal(value);
        throw new NotSupportedException($"Unsupported event type {eventType.FullName}");
    }
}
