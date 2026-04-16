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

    /// <inheritdoc/>
    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(id.Value, _ => new InMemoryStream());

        if (!stream.TryAppend(events, expectedVersion.Value, out var newVersion))
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
        // Special handling for "$all" pseudo-stream: reads from all streams in order
        if (string.Equals(id.Value, "$all", StringComparison.Ordinal) || string.Equals(id.Value, "*", StringComparison.Ordinal))
        {
            var allEvents = new List<RawEvent>();
            foreach (var stream in _streams.Values)
            {
                allEvents.AddRange(stream.ReadFrom(from.Value));
            }

            // Sort by position to maintain ordering across streams
            allEvents.Sort((a, b) => a.Position.Value.CompareTo(b.Position.Value));

            foreach (var e in allEvents)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
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
