using ZeroAlloc.Collections;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>Non-generic base interface for <see cref="Aggregate{TId,TState}"/>. Used for repository constraints.</summary>
public interface IAggregate
{
    /// <summary>Applies a historic event during stream replay.</summary>
    internal void ApplyHistoric(object @event, StreamPosition position);

    /// <summary>Returns and clears the uncommitted event queue.</summary>
    internal ReadOnlySpan<object> DequeueUncommitted();

    /// <summary>The version at which this aggregate was loaded. Used for optimistic concurrency.</summary>
    StreamPosition OriginalVersion { get; }
}

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

    /// <summary>The current aggregate state. Updated on every <see cref="Raise{TEvent}"/> and <see cref="IAggregate.ApplyHistoric"/>.</summary>
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

    void IAggregate.ApplyHistoric(object @event, StreamPosition position)
    {
        State = ApplyEvent(State, @event);
        Version = position;
        OriginalVersion = position;
    }

    ReadOnlySpan<object> IAggregate.DequeueUncommitted()
    {
        if (_uncommitted.Count == 0) return ReadOnlySpan<object>.Empty;
        var snapshot = _uncommitted.AsReadOnlySpan().ToArray(); // snapshot before clear
        _uncommitted.Clear();
        return snapshot;
    }

    // Internal convenience wrappers so tests and repository code can call without a cast
    internal void ApplyHistoric(object @event, StreamPosition position) =>
        ((IAggregate)this).ApplyHistoric(@event, position);

    internal ReadOnlySpan<object> DequeueUncommitted() =>
        ((IAggregate)this).DequeueUncommitted();

    /// <summary>
    /// Routes an event to the correct state transition. Implemented by the source generator
    /// (ZeroAlloc.EventSourcing.Generators) for <c>partial</c> aggregate classes.
    /// </summary>
    protected abstract TState ApplyEvent(TState state, object @event);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uncommitted.Dispose();
        GC.SuppressFinalize(this);
    }
}
