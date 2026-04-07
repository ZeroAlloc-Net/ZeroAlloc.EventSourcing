namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Strategy for when to commit consumer position to checkpoint store.
/// </summary>
public enum CommitStrategy
{
    /// <summary>Commit position after each event processed</summary>
    AfterEvent = 0,

    /// <summary>Commit position after entire batch processed (default, better performance)</summary>
    AfterBatch = 1,

    /// <summary>Manual commits (application calls CommitAsync explicitly)</summary>
    Manual = 2,
}
