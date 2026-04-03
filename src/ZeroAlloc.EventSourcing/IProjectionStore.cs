namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Abstraction for storing and retrieving projection state (read models).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IProjectionStore"/> decouples projection persistence from projection logic.
/// Implementations handle key-value storage of arbitrary projection state, enabling:
/// <list type="bullet">
/// <item><description>Persistence of materialized read models across restarts</description></item>
/// <item><description>Multiple independent projections over the same event stream</description></item>
/// <item><description>Rebuilding projections from stored state or from scratch</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>No concurrency guarantees.</strong> Implementations may assume single-threaded access
/// or implement their own synchronization. Consumers are responsible for external coordination
/// if multiple threads access the store concurrently.
/// </para>
/// </remarks>
public interface IProjectionStore
{
    /// <summary>
    /// Saves the projection state under the given key.
    /// </summary>
    /// <remarks>
    /// If a value already exists for the key, it is overwritten. No versioning or conflict detection is performed.
    /// </remarks>
    /// <param name="key">A unique identifier for the projection (e.g., "OrderProjection", "InventoryProjection").</param>
    /// <param name="state">The serialized projection state (e.g., JSON string, byte array).</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    ValueTask SaveAsync(string key, string state, CancellationToken ct = default);

    /// <summary>
    /// Loads the projection state for the given key.
    /// </summary>
    /// <remarks>
    /// Returns null if no state exists for the key (e.g., first run, or state has been cleared).
    /// </remarks>
    /// <param name="key">A unique identifier for the projection.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>The serialized projection state, or null if not found.</returns>
    ValueTask<string?> LoadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Clears the stored projection state for the given key.
    /// </summary>
    /// <remarks>
    /// Idempotent: clearing a non-existent key has no effect.
    /// </remarks>
    /// <param name="key">A unique identifier for the projection.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    ValueTask ClearAsync(string key, CancellationToken ct = default);
}
