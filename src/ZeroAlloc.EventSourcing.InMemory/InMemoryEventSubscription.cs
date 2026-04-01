using ZeroAlloc.AsyncEvents;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// Represents a live subscription to an <see cref="InMemoryStream"/>.
/// Call <see cref="StartAsync"/> to begin receiving events; dispose to unregister.
/// </summary>
internal sealed class InMemoryEventSubscription : IEventSubscription
{
    private readonly InMemoryStream _stream;
    private readonly AsyncEvent<RawEvent> _callback;
    private bool _running;
    private bool _disposed;

    internal InMemoryEventSubscription(InMemoryStream stream, AsyncEvent<RawEvent> callback)
    {
        _stream = stream;
        _callback = callback;
    }

    /// <inheritdoc/>
    public bool IsRunning => _running && !_disposed;

    /// <inheritdoc/>
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        _running = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _running = false;
        _stream.Unsubscribe(_callback);
        return ValueTask.CompletedTask;
    }
}
