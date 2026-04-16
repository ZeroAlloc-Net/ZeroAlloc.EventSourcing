using ZeroAlloc.Collections;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Base class for DDD aggregate roots. Separates identity/versioning (this class) from domain
/// state (<typeparamref name="TState"/>, a struct) for allocation-free state transitions.
/// Uncommitted events are pooled via <see cref="HeapPooledList{T}"/>.
/// </summary>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
/// <typeparam name="TState">The aggregate state type. Must be a struct implementing <see cref="IAggregateState{TSelf}"/>.</typeparam>
public abstract class Aggregate<TId, TState> : IAggregate, IDisposable
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    private readonly HeapPooledList<object> _uncommitted = new();
    private bool _disposed;

    /// <summary>The aggregate identifier.</summary>
    public TId Id { get; protected set; }

    /// <summary>The current version — equal to the number of events applied (including uncommitted).</summary>
    public StreamPosition Version { get; private set; } = StreamPosition.Start;

    /// <summary>The version when this aggregate was loaded from the store. Used for optimistic concurrency on save.</summary>
    public StreamPosition OriginalVersion { get; private set; } = StreamPosition.Start;

    /// <summary>
    /// The current aggregate state. Read-only externally — mutation only occurs via <see cref="Raise{TEvent}"/>
    /// and <see cref="ApplyHistoric"/>. Exposed publicly so consumers can read state for queries and projections
    /// without requiring a separate read model layer.
    /// </summary>
    public TState State { get; private set; } = TState.Initial;

    /// <summary>
    /// Raises a new domain event: adds it to the uncommitted queue and applies it to state immediately.
    /// Call this from command methods on the concrete aggregate.
    /// </summary>
    protected void Raise<TEvent>(TEvent @event) where TEvent : notnull
    {
        _uncommitted.Add(@event);
        State = ApplyEvent(State, @event);
        Version = Version.Next();
    }

    /// <summary>Applies a historic event during stream replay. Does NOT add to the uncommitted queue.</summary>
    internal void ApplyHistoric(object @event, StreamPosition position)
    {
        State = ApplyEvent(State, @event);
        Version = position;
        OriginalVersion = position;
    }

    /// <summary>Returns the uncommitted events as a span and clears the queue. Snapshots to array before clear for pool safety.</summary>
    internal ReadOnlySpan<object> DequeueUncommitted()
    {
        if (_uncommitted.Count == 0) return ReadOnlySpan<object>.Empty;
        var snapshot = _uncommitted.AsReadOnlySpan().ToArray(); // snapshot before clear — pool buffer is returned by Clear()
        _uncommitted.Clear();
        return snapshot;
    }

    internal void AcceptVersion(StreamPosition position) => OriginalVersion = position;

    // Explicit interface implementations delegate to the internal methods above
    void IAggregate.ApplyHistoric(object @event, StreamPosition position) => ApplyHistoric(@event, position);
    ReadOnlySpan<object> IAggregate.DequeueUncommitted() => DequeueUncommitted();
    void IAggregate.AcceptVersion(StreamPosition position) => AcceptVersion(position);

    /// <summary>
    /// Routes an event to the correct state transition. Implemented by the source generator
    /// (ZeroAlloc.EventSourcing.Generators) for <c>partial</c> aggregate classes.
    /// </summary>
    protected abstract TState ApplyEvent(TState state, object @event);

    /// <inheritdoc/>
    /// <remarks>Not thread-safe. Aggregate ownership is expected to be single-threaded.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uncommitted.Dispose();
        GC.SuppressFinalize(this);
    }
}
