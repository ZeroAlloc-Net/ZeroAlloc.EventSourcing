using System.Buffers;
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
    public async IAsyncEnumerable<EventEnvelope> ReadAsync(
        StreamId id,
        StreamPosition from = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var raw in _adapter.ReadAsync(id, from, ct).ConfigureAwait(false))
        {
            if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
                continue; // unknown event type — skip for forward compatibility

            yield return ToEnvelope(id, raw, type);
        }
    }

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
        return ReadBatchCoreAsync(id, from, maxCount, ct);
    }

    private async IAsyncEnumerable<EventEnvelope> ReadBatchCoreAsync(
        StreamId id,
        StreamPosition from,
        int maxCount,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var yielded = 0;
        while (true)
        {
            var requested = maxCount - yielded;
            var pageStart = from;
            var rows = 0;
            await foreach (var raw in _adapter.ReadBatchAsync(id, from, requested, ct).ConfigureAwait(false))
            {
                rows++;
                from = raw.Position;
                if (!_registry.TryGetType(raw.EventType, out var type) || type is null)
                    continue; // unknown event type — skip for forward compatibility

                yield return ToEnvelope(id, raw, type);
                yielded++;
            }

            // A short page is the end of the stream. A full page that held unknown types leaves
            // the batch short, so the rows after it are read.
            if (yielded == maxCount || rows < requested)
                yield break;

            // The next page starts after the last row of this one; an adapter whose positions do
            // not advance would otherwise be read again from the same place forever.
            if (from.Value <= pageStart.Value)
                throw new InvalidOperationException(
                    $"The adapter returned events at or before position {pageStart.Value} for a read after it, so the read cannot advance.");
        }
    }

    private EventEnvelope ToEnvelope(StreamId id, RawEvent raw, Type type)
    {
        var deserialized = _serializer.Deserialize(raw.Payload, type);
        if (_upcasterPipeline?.TryUpcast(deserialized, out var upgraded) == true)
            deserialized = upgraded;
        return new EventEnvelope(id, raw.Position, deserialized, raw.Metadata);
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
