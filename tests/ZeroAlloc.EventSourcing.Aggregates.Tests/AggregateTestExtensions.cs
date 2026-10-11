namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

internal static class AggregateTestExtensions
{
    /// <summary>Drains the uncommitted events of <paramref name="aggregate"/> into a new array.</summary>
    public static object[] DequeueUncommitted(this IAggregate aggregate)
    {
        var events = new object[aggregate.UncommittedCount];
        aggregate.DrainUncommitted(events);
        return events;
    }
}
