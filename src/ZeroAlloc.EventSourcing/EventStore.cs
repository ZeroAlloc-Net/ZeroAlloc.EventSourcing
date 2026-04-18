using System.Runtime.CompilerServices;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Default <see cref="IEventStore"/> implementation. Wraps an <see cref="IEventStoreAdapter"/>
/// with serialization via <see cref="IEventSerializer"/> and type mapping via <see cref="IEventTypeRegistry"/>.
/// </summary>
public sealed class EventStore : IEventStore
{
    private readonly IEventStoreAdapter _adapter;
    private readonly IEventSerializer _serializer;
    private readonly IEventTypeRegistry _registry;
    private readonly IUpcasterPipeline? _upcasterPipeline;

    /// <summary>Initialises an <see cref="EventStore"/> with the given adapter, serializer, and type registry.</summary>
    public EventStore(
        IEventStoreAdapter adapter,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IUpcasterPipeline? upcasterPipeline = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(registry);
        _adapter = adapter;
        _serializer = serializer;
        _registry = registry;
        _upcasterPipeline = upcasterPipeline;
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<object> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        // Phase 1: allocates a RawEvent[] per append. Phase 3 should use ArrayPool<RawEvent> or stackalloc
        // with an inline threshold to satisfy the zero-alloc mandate for production adapters.
        var raw = new RawEvent[events.Length];
        for (var i = 0; i < events.Length; i++)
        {
            var e = events.Span[i];
            var typeName = _registry.GetTypeName(e.GetType());
            raw[i] = new RawEvent(
                // Position is advisory — SQL adapters must assign their own positions via DB sequence/trigger and ignore this field.
                // Positions are 1-based: the first event in a stream is at position 1, matching the stream version after append.
                new StreamPosition(expectedVersion.Value + i + 1),
                typeName,
                _serializer.Serialize(e),
                EventMetadata.New(typeName));
        }

        return await _adapter.AppendAsync(id, raw.AsMemory(), expectedVersion, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var raw in _adapter.ReadAsync(id, from, ct).ConfigureAwait(false))
        {
            if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
                continue; // unknown event type — skip for forward compatibility

            var deserialized = _serializer.Deserialize(raw.Payload, type);
            if (_upcasterPipeline?.TryUpcast(deserialized, out var upgraded) == true)
                deserialized = upgraded;
            yield return new EventEnvelope(id, raw.Position, deserialized, raw.Metadata);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<EventEnvelope, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        return await _adapter.SubscribeAsync(id, from, async (raw, token) =>
        {
            if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
                return;

            var deserialized = _serializer.Deserialize(raw.Payload, type);
            if (_upcasterPipeline?.TryUpcast(deserialized, out var upgraded) == true)
                deserialized = upgraded;
            var envelope = new EventEnvelope(id, raw.Position, deserialized, raw.Metadata);
            await handler(envelope, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
