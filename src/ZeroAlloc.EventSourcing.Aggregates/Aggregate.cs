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
public abstract class Aggregate<TId, TState> : IAggregate, IAggregateIdentity<TId>, IDisposable
    where TId : struct
    where TState : struct, IAggregateState<TState>
{
    private readonly HeapPooledList<object> _uncommitted = new();
    private bool _disposed;

    // Set once anything has put state on this aggregate: a raised, replayed or restored event.
    // RestoreState is only legal while it is false.
    private bool _hasHistory;

    /// <summary>The aggregate identifier.</summary>
    /// <remarks>
    /// <see cref="AggregateRepository{TAggregate,TId}"/> and
    /// <see cref="SnapshotCachingRepositoryDecorator{TAggregate,TId,TState}"/> set it to the id passed
    /// to <c>LoadAsync</c>, so a loaded aggregate always carries the id of the stream it was loaded from.
    /// A new aggregate keeps <c>default</c> until the aggregate sets it itself.
    /// </remarks>
    public TId Id { get; protected set; }

    /// <summary>The current version — equal to the number of events applied (including uncommitted).</summary>
    public StreamPosition Version { get; private set; } = StreamPosition.Start;

    /// <summary>The version when this aggregate was loaded from the store. Used for optimistic concurrency on save.</summary>
    public StreamPosition OriginalVersion { get; private set; } = StreamPosition.Start;

    /// <summary>
    /// The current aggregate state. Read-only externally — mutation only occurs via <see cref="Raise{TEvent}"/>,
    /// <see cref="ApplyHistoric"/> and <see cref="RestoreState"/>. Exposed publicly so consumers can read state for queries and projections
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
        _hasHistory = true;
    }

    /// <summary>Applies a historic event during stream replay. Does NOT add to the uncommitted queue.</summary>
    internal void ApplyHistoric(object @event, StreamPosition position)
    {
        State = ApplyEvent(State, @event);
        Version = position;
        OriginalVersion = position;
        _hasHistory = true;
    }

    /// <summary>
    /// Restores the aggregate from a snapshot: sets <see cref="State"/> to <paramref name="state"/>
    /// and both <see cref="Version"/> and <see cref="OriginalVersion"/> to <paramref name="position"/>,
    /// exactly as replaying the events up to <paramref name="position"/> would have left them.
    /// No events are queued, so a following save appends only what is raised after the restore,
    /// with <paramref name="position"/> (or the position of the last replayed event) as its expected version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the <c>restoreState</c> callback of
    /// <see cref="SnapshotCachingRepositoryDecorator{TAggregate,TId,TState}"/> calls:
    /// <c>restoreState: (order, state, pos) =&gt; order.RestoreState(state, pos)</c>.
    /// It is public so that callback can be written outside the aggregate class.
    /// </para>
    /// <para>
    /// Only a fresh aggregate can be restored: one that has not raised, replayed or restored anything.
    /// Restoring any other aggregate would overwrite state that events already produced, so it throws.
    /// </para>
    /// </remarks>
    /// <param name="state">The snapshot state.</param>
    /// <param name="position">The stream position the snapshot was taken at.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">
    /// The aggregate has already raised or replayed events, or has already been restored.
    /// </exception>
    public void RestoreState(TState state, StreamPosition position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position.Value, nameof(position));
        if (_hasHistory)
            throw new InvalidOperationException(
                $"{GetType().Name} can only be restored from a snapshot while it is fresh, before any event is raised, replayed or restored. " +
                $"It is at version {Version.Value}.");

        State = state;
        Version = position;
        OriginalVersion = position;
        _hasHistory = true;
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
    void IAggregateIdentity<TId>.AssignId(TId id) => Id = id;

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
