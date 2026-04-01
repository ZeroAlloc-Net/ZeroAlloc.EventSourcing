namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Marker interface for aggregate state types. Implementations must be value types (structs)
/// to ensure state transitions are allocation-free on the hot path.
/// </summary>
/// <typeparam name="TSelf">The implementing state type (CRTP pattern).</typeparam>
public interface IAggregateState<TSelf> where TSelf : struct, IAggregateState<TSelf>
{
    /// <summary>Returns the initial (empty) state for a newly created aggregate.</summary>
    static abstract TSelf Initial { get; }
}
