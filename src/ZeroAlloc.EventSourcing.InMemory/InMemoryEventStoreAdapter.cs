using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// In-memory <see cref="IEventStoreAdapter"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// This is a test double — it is not a production adapter. Allocations such as list snapshots in
/// <see cref="InMemoryStream.ReadFrom"/> are intentional and acceptable in a test context.
/// </summary>
public sealed class InMemoryEventStoreAdapter : IEventStoreAdapter
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new(StringComparer.Ordinal);
    // Monotonic, adapter-wide counter incremented once per appended event under the per-stream
    // write lock. Backs reads from <see cref="StreamId.Global"/> — each stored entry carries the
    // global position assigned at append time. Interlocked.Increment is AOT-safe.
    private long _globalCounter;

    /// <inheritdoc/>
    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());

        // The assignment callback runs once, under the stream's write lock, and reserves a
        // contiguous range of global positions. Using Interlocked.Add keeps the reservation
        // atomic across concurrent appends to different streams.
        long AssignGlobalPositions(int count)
        {
            // Reserve [next, next+count). Returns the first reserved position (1-based).
            var last = Interlocked.Add(ref _globalCounter, count);
            return last - count + 1;
        }

        if (!stream.TryAppend(events, expectedVersion.Value, AssignGlobalPositions, out var newVersion))
        {
            var error = StoreError.Conflict(id, expectedVersion, new StreamPosition(newVersion));
            return ValueTask.FromResult(Result<AppendResult, StoreError>.Failure(error));
        }

        var result = new AppendResult(id, new StreamPosition(newVersion));
        return ValueTask.FromResult(Result<AppendResult, StoreError>.Success(result));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
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
            // Collect a point-in-time snapshot across all streams, then sort by global position.
            // The per-stream snapshots are each taken under their own lock; the merged view is a
            // consistent union of the per-stream prefixes observed during this call.
            var merged = new List<InMemoryStream.StoredEntry>();
            foreach (var stream in _streams.Values)
            {
                merged.AddRange(stream.SnapshotEntries());
            }

            merged.Sort(static (a, b) => a.GlobalPosition.CompareTo(b.GlobalPosition));

            foreach (var entry in merged)
            {
                if (entry.GlobalPosition <= from.Value) continue;
                ct.ThrowIfCancellationRequested();
                // Yield with Position = global position — the semantic overload from the design.
                yield return entry.RawEvent with { Position = new StreamPosition(entry.GlobalPosition) };
            }

            yield break;
        }

        if (!_streams.TryGetValue(id.Value, out var specificStream))
            yield break;

        foreach (var e in specificStream.ReadFrom(from.Value))
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
