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
/// State is stored as bytes, so any <c>ZeroAlloc.Serialisation</c> serializer can write it: a text
/// serializer such as System.Text.Json writes UTF-8 bytes, and a binary serializer such as MemoryPack
/// or MessagePack writes its own format. The store keeps the bytes exactly as given.
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
    /// The store must not keep a reference to <paramref name="state"/> after the returned task completes: the
    /// caller may reuse the buffer behind it, so an in-memory store copies the bytes.
    /// </remarks>
    /// <param name="key">A unique identifier for the projection (e.g., "OrderProjection", "InventoryProjection").</param>
    /// <param name="state">The serialized projection state, for example UTF-8 JSON or MemoryPack bytes.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    ValueTask SaveAsync(string key, ReadOnlyMemory<byte> state, CancellationToken ct = default);

    /// <summary>
    /// Loads the projection state for the given key.
    /// </summary>
    /// <remarks>
    /// Returns null if no state exists for the key (e.g., first run, or state has been cleared).
    /// </remarks>
    /// <param name="key">A unique identifier for the projection.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>
    /// The serialized projection state as it was saved, or <see langword="null"/> if not found. The bytes are
    /// read-only; a store may hand out its own copy without copying it again.
    /// </returns>
    ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string key, CancellationToken ct = default);

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
