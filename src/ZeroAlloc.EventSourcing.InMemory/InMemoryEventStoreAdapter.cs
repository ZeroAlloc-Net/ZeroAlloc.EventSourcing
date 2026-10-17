using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory <see cref="IEventStoreAdapter"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// This is a test double — it is not a production adapter. Allocations such as the array snapshot
/// each read returns are intentional and acceptable in a test context.
/// </summary>
public sealed class InMemoryEventStoreAdapter : IEventStoreAdapter
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new(StringComparer.Ordinal);

    // Every appended event in global-position order, backing reads from <see cref="StreamId.Global"/>.
    // Appends take _globalLock around reserving their positions and adding to this list, so the list
    // has no gaps and _globalLog[i] holds global position i + 1. A reader can then never see a
    // position before every lower one is visible, and a read finds its start by index instead of
    // merging and sorting every stream. Appends to different streams are serialized by the lock,
    // which is acceptable for a test double.
    private readonly List<InMemoryStream.StoredEntry> _globalLog = new();
#if NET9_0_OR_GREATER
    private readonly Lock _globalLock = new();
#else
    private readonly object _globalLock = new();
#endif

    /// <inheritdoc/>
    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());

        // Runs once, under the stream's write lock, while _globalLock is held: the next global
        // positions are the ones after the last logged event, so the reservation is contiguous.
        long firstGlobal = 0;
        long AssignGlobalPositions(int count)
        {
            firstGlobal = _globalLog.Count + 1;
            return firstGlobal;
        }

        bool appended;
        long newVersion;
        RawEvent[]? toBroadcast;
        lock (_globalLock)
        {
            appended = stream.TryAppend(events, expectedVersion.Value, AssignGlobalPositions, out newVersion, out toBroadcast);
            if (appended)
            {
                var span = events.Span;
                for (var i = 0; i < span.Length; i++)
                    _globalLog.Add(new InMemoryStream.StoredEntry(firstGlobal + i, span[i]));
            }
        }

        if (!appended)
        {
            var error = StoreError.Conflict(id, expectedVersion, new StreamPosition(newVersion));
            return ValueTask.FromResult(Result<AppendResult, StoreError>.Failure(error));
        }

        // Outside every lock, so a subscriber that reads or appends cannot deadlock.
        if (toBroadcast is not null)
            stream.Broadcast(toBroadcast);

        var result = new AppendResult(id, new StreamPosition(newVersion));
        return ValueTask.FromResult(Result<AppendResult, StoreError>.Success(result));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        CancellationToken ct = default)
        => Read(id, from, int.MaxValue, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Copies only the events of the batch, so a consumer paging through a long stream pays for each
    /// page, not for the whole stream on every page.
    /// </remarks>
    IAsyncEnumerable<RawEvent> IEventStoreAdapter.ReadBatchAsync(
        StreamId id,
        StreamPosition from,
        int maxCount,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return Read(id, from, maxCount, ct);
    }

    private async IAsyncEnumerable<RawEvent> Read(
        StreamId id,
        StreamPosition from,
        int maxCount,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Global stream: yield events from all streams ordered by the global position assigned
        // at append time. <paramref name="from"/> is interpreted as an EXCLUSIVE lower bound
        // on the global position — entries with GlobalPosition > from.Value are returned.
        // This matches the per-stream EXCLUSIVE convention (Skip-based) and is required by
        // StreamConsumer, which checkpoints to the position of the last delivered event and
        // would otherwise re-deliver the same event indefinitely.
        // The legacy "$all" alias is intentionally NOT recognised here; "*" / StreamId.Global
        // is the canonical identity per Task 1's design.
        if (id.IsGlobal)
        {
            // A point-in-time copy of the requested page. _globalLog[i] holds global position
            // i + 1, so the first entry after the exclusive bound from.Value is at index from.Value.
            InMemoryStream.StoredEntry[] page;
            lock (_globalLock)
            {
                var start = (int)Math.Clamp(from.Value, 0, _globalLog.Count);
                page = new InMemoryStream.StoredEntry[Math.Min(maxCount, _globalLog.Count - start)];
                _globalLog.CopyTo(start, page, 0, page.Length);
            }

            foreach (var entry in page)
            {
                ct.ThrowIfCancellationRequested();
                // Yield with Position = global position — the semantic overload from the design.
                yield return entry.RawEvent with { Position = new StreamPosition(entry.GlobalPosition) };
            }

            yield break;
        }

        if (!_streams.TryGetValue(id.Value, out var specificStream))
            yield break;

        foreach (var e in specificStream.ReadRange(from.Value, maxCount))
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    /// <inheritdoc/>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        // GetOrAdd intentionally creates the stream if absent — subscribers may register before any events are appended.
        // This differs from ReadAsync which uses TryGetValue and yields nothing for non-existent streams.
        var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());

        // NOTE: 'sub' is null between stream.Subscribe(callback) and the assignment below.
        // Any event delivered in this window will see sub?.IsRunning == false and be dropped.
        // This is intentional — events before StartAsync() are always dropped by design.
        // sub is captured by the callback; the closure checks IsRunning before dispatching
        // so that events arriving before StartAsync or after DisposeAsync are silently dropped.
        InMemoryEventSubscription? sub = null;
        var callback = new ZeroAlloc.AsyncEvents.AsyncEvent<RawEvent>(async (e, token) =>
        {
            if (sub?.IsRunning == true)
                await handler(e, token).ConfigureAwait(false);
        });

        stream.Subscribe(callback);
        sub = new InMemoryEventSubscription(stream, callback);
        return ValueTask.FromResult<IEventSubscription>(sub);
    }
}
