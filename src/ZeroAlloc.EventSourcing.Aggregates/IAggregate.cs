using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>Non-generic base interface for <see cref="Aggregate{TId,TState}"/>. Used for repository constraints.</summary>
public interface IAggregate
{
    /// <summary>Applies a historic event during stream replay.</summary>
    internal void ApplyHistoric(object @event, StreamPosition position);

    /// <summary>Returns and clears the uncommitted event queue.</summary>
    internal ReadOnlySpan<object> DequeueUncommitted();

    /// <summary>Updates <see cref="OriginalVersion"/> to <paramref name="position"/> after a successful save.</summary>
    internal void AcceptVersion(StreamPosition position);

    /// <summary>The version at which this aggregate was loaded. Used for optimistic concurrency.</summary>
    StreamPosition OriginalVersion { get; }
}
