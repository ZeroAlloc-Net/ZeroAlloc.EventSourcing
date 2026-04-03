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

/// <summary>
/// Verifies that a projection's state is consistent with the event store.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProjectionConsistencyChecker{TReadModel}"/> allows you to detect and report
/// projection gaps—situations where the projection has fallen behind the event store and
/// needs rebuilding or catching up. This is essential for maintaining read model freshness
/// and detecting failures in projection pipelines.
/// </para>
/// <para>
/// The checker compares the projection's last known position with the latest event position
/// in the store. If they match, the projection is considered up-to-date. Otherwise, it is
/// out of sync and requires intervention (rebuild, catch-up, or inspection).
/// </para>
/// </remarks>
/// <typeparam name="TReadModel">The read model type (unused directly but kept for type safety).</typeparam>
/// <example>
/// <code>
/// var checker = new ProjectionConsistencyChecker&lt;OrderSummary&gt;();
/// var report = await checker.VerifyAsync(eventStore, streamId, lastKnownPosition);
///
/// if (!report.IsConsistent)
/// {
///     Console.WriteLine($"Projection is behind: {report.DiscrepancyReason}");
///     // Trigger a rebuild or catch-up
/// }
/// </code>
/// </example>
public class ProjectionConsistencyChecker<TReadModel>
{
    /// <summary>
    /// Verifies that the projection is consistent with the event store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method:
    /// <list type="number">
    /// <item><description>Queries the event store for the latest event position in the stream</description></item>
    /// <item><description>Compares it with the projection's last known checkpoint</description></item>
    /// <item><description>Returns a report indicating whether the projection is up-to-date</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// If the projection checkpoint is null (first run or no saved state), the projection is
    /// considered inconsistent (behind) since it has not processed any events yet.
    /// </para>
    /// </remarks>
    /// <param name="eventStore">The event store to query for the latest position.</param>
    /// <param name="streamId">The stream to check consistency for.</param>
    /// <param name="projectionCheckpoint">The last position processed by the projection, or null if no state exists.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A consistency report with details about the projection's state relative to the event store.</returns>
    public async ValueTask<ProjectionConsistencyReport?> VerifyAsync(
        IEventStore eventStore,
        StreamId streamId,
        StreamPosition? projectionCheckpoint,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // If no checkpoint exists, the projection has no saved state
        if (projectionCheckpoint == null)
        {
            return null;
        }

        // Find the latest event position by reading the stream
        StreamPosition latestPosition = StreamPosition.Start;
        await foreach (var @event in eventStore.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            ct.ThrowIfCancellationRequested();
            latestPosition = @event.Position;
        }

        // Compare positions
        var isConsistent = projectionCheckpoint.Value.Value == latestPosition.Value;
        var discrepancyReason = isConsistent
            ? null
            : $"Projection is behind: at position {projectionCheckpoint.Value.Value}, event store is at {latestPosition.Value}";

        return new ProjectionConsistencyReport(
            IsConsistent: isConsistent,
            ProjectionCheckpoint: projectionCheckpoint,
            EventStoreLatestPosition: latestPosition,
            DiscrepancyReason: discrepancyReason);
    }
}
