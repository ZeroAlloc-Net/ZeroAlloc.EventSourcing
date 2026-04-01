using ZeroAlloc.AsyncEvents;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// Thread-safe, append-only event list for a single stream.
/// This type is a test double — it is not intended for production use.
/// </summary>
internal sealed class InMemoryStream
{
    private readonly List<RawEvent> _events = new();
    private long _version = 0;
    // AsyncEventHandler<T> is internally thread-safe via lock-free CAS (Interlocked.CompareExchange)
    // for Register/Unregister/InvokeAsync — no external locking needed.
    private AsyncEventHandler<RawEvent> _broadcast = new(InvokeMode.Sequential);

    /// <summary>
    /// Current version (number of events appended).
    /// <c>Interlocked.Read</c> is used here because <c>long</c> reads are not atomic on 32-bit
    /// platforms without it; all writes happen inside <c>lock (_events)</c>, which provides the
    /// necessary memory barrier on the write side. Reads inside the lock use the plain field directly
    /// (the lock itself guarantees visibility there).
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Appends events atomically if <paramref name="expectedVersion"/> matches the current version.
    /// Returns true and sets <paramref name="newVersion"/> on success; returns false on conflict.
    /// </summary>
    public bool TryAppend(ReadOnlyMemory<RawEvent> incoming, long expectedVersion, out long newVersion)
    {
        RawEvent[]? toFire = null;
        lock (_events)
        {
            if (_version != expectedVersion)
            {
                newVersion = _version;
                return false;
            }

            foreach (var e in incoming.Span)
                _events.Add(e);

            _version += incoming.Length;
            newVersion = _version;

            // Capture events to fire outside the lock
            if (_broadcast.Count > 0)
            {
                toFire = incoming.ToArray();
            }
        }

        // Fire events outside the lock — fire-and-forget
        if (toFire is not null)
        {
            foreach (var e in toFire)
                _ = _broadcast.InvokeAsync(e);
        }

        return true;
    }

    /// <summary>
    /// Registers <paramref name="callback"/> to receive events as they are appended.
    /// The caller already holds the callback reference and passes it back to <see cref="Unsubscribe"/> directly.
    /// </summary>
    public void Subscribe(AsyncEvent<RawEvent> callback)
        => _broadcast.Register(callback);

    /// <summary>Unregisters a previously registered callback.</summary>
    public void Unsubscribe(AsyncEvent<RawEvent> callback)
        => _broadcast.Unregister(callback);

    /// <summary>Returns a snapshot of events starting at <paramref name="fromPosition"/>.</summary>
    public IEnumerable<RawEvent> ReadFrom(long fromPosition)
    {
        lock (_events)
        {
            // Cast is safe: InMemoryStream is a test double; streams with >int.MaxValue events are not a supported scenario.
            // Allocates a list snapshot intentionally — InMemoryStream is a test double, not a production adapter.
            return _events.Skip((int)fromPosition).ToList();
        }
    }
}
