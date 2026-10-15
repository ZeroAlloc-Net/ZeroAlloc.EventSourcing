using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Default <see cref="IEventStore"/> implementation. Wraps an <see cref="IEventStoreAdapter"/>
/// with serialization via <see cref="IEventSerializer"/> and type mapping via <see cref="IEventTypeRegistry"/>.
/// </summary>
public sealed partial class EventStore : IEventStore
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
        // The raw events live in a pooled buffer for the duration of the adapter call only:
        // IEventStoreAdapter.AppendAsync must not keep the memory once its task has completed.
        var count = events.Length;
        var raw = ArrayPool<RawEvent>.Shared.Rent(count);
        try
        {
            var source = events.Span;
            for (var i = 0; i < count; i++)
            {
                var e = source[i];
                var typeName = _registry.GetTypeName(e.GetType());
                raw[i] = new RawEvent(
                    // Position is advisory — SQL adapters must assign their own positions via DB sequence/trigger and ignore this field.
                    // Positions are 1-based: the first event in a stream is at position 1, matching the stream version after append.
                    new StreamPosition(expectedVersion.Value + i + 1),
                    typeName,
                    _serializer.Serialize(e),
                    EventMetadata.New(typeName));
            }

            return await _adapter.AppendAsync(id, new ReadOnlyMemory<RawEvent>(raw, 0, count), expectedVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            // Drop the payload and type name references before the buffer goes back to the pool.
            raw.AsSpan(0, count).Clear();
            ArrayPool<RawEvent>.Shared.Return(raw);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Each call allocates one small reader object, which is also the enumerator for the first
    /// enumeration on the calling thread. Nothing is allocated per event by the store itself.
    /// </remarks>
    public IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        CancellationToken ct = default)
        => new StreamReader(this, id, from, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Each query the adapter runs is bounded to <paramref name="maxCount"/> rows through
    /// <see cref="IEventStoreAdapter.ReadBatchAsync"/>. Events of a type the registry does not know
    /// are skipped, as in <see cref="ReadAsync"/>; when that leaves the batch short, the next rows are
    /// read, so a short batch still means the end of the stream.
    /// </remarks>
    public IAsyncEnumerable<EventEnvelope> ReadBatchAsync(
        StreamId id,
        StreamPosition from,
        int maxCount,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return new BatchReader(this, id, from, maxCount, ct);
    }

    /// <summary>
    /// Deserializes <paramref name="raw"/> when the registry knows its type, then upcasts it.
    /// Returns <see langword="false"/> for an unknown type, which readers skip for forward
    /// compatibility.
    /// </summary>
    private bool TryDeserialize(in RawEvent raw, [NotNullWhen(true)] out object? @event)
    {
        if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
        {
            @event = null;
            return false;
        }

        var deserialized = _serializer.Deserialize(raw.Payload, type);
        if (_upcasterPipeline?.TryUpcast(deserialized, out var upgraded) == true)
            deserialized = upgraded;
        @event = deserialized;
        return true;
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
            if (!TryDeserialize(raw, out var deserialized))
                return;

            var envelope = new EventEnvelope(id, raw.Position, deserialized, raw.Metadata);
            await handler(envelope, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
