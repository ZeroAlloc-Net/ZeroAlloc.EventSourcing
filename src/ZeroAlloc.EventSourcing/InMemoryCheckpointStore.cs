using System.Collections.Concurrent;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// In-memory checkpoint store for testing and single-process applications.
/// Positions are lost on application restart.
/// NOT suitable for production distributed systems.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    /// <summary>Concurrent dictionary storing position by consumer ID.</summary>
    private readonly ConcurrentDictionary<string, StreamPosition> _positions = new();

    /// <inheritdoc cref="ICheckpointStore.ReadAsync"/>
    /// <remarks>Cancellation is not supported in this synchronous in-memory implementation.</remarks>
    public Task<StreamPosition?> ReadAsync(string consumerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        var result = _positions.TryGetValue(consumerId, out var position) ? position : (StreamPosition?)null;
        return Task.FromResult(result);
    }

    /// <inheritdoc cref="ICheckpointStore.WriteAsync"/>
    /// <remarks>Cancellation is not supported in this synchronous in-memory implementation.</remarks>
    public Task WriteAsync(string consumerId, StreamPosition position, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _positions[consumerId] = position;
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="ICheckpointStore.DeleteAsync"/>
    /// <remarks>Cancellation is not supported in this synchronous in-memory implementation.</remarks>
    public Task DeleteAsync(string consumerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("Consumer ID cannot be null or whitespace", nameof(consumerId));

        _positions.TryRemove(consumerId, out _);
        return Task.CompletedTask;
    }
}
