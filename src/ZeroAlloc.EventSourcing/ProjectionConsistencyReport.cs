namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Report of a projection consistency check.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProjectionConsistencyReport"/> summarizes the result of verifying a projection's
/// state against the event store. If <see cref="IsConsistent"/> is false, the report includes
/// a discrepancy reason explaining why the projection is out of sync.
/// </para>
/// </remarks>
/// <param name="IsConsistent">true if the projection is in sync with the event store; false otherwise.</param>
/// <param name="ProjectionCheckpoint">The position of the last event processed by the projection (if known).</param>
/// <param name="EventStoreLatestPosition">The position of the latest event in the event store.</param>
/// <param name="DiscrepancyReason">A human-readable explanation of why the projection is inconsistent (null if consistent).</param>
public record ProjectionConsistencyReport(
    bool IsConsistent,
    StreamPosition? ProjectionCheckpoint,
    StreamPosition EventStoreLatestPosition,
    string? DiscrepancyReason);
