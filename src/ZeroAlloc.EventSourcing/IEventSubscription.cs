namespace ZeroAlloc.EventSourcing;

/// <summary>Represents an active event stream subscription. Dispose to stop receiving events.</summary>
public interface IEventSubscription : IAsyncDisposable
{
    /// <summary>Starts delivering events to the registered handler.</summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Returns true if the subscription has been started and not yet disposed.</summary>
    bool IsRunning { get; }
}
