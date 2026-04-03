using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates;

/// <summary>
/// Decorator that wraps an <see cref="IAggregateRepository{TAggregate,TId}"/> to optimize loading
/// using snapshots. Checks for snapshots, restores state, and replays only events after the
/// snapshot position according to the configured strategy.
///
/// Implements the Liskov Substitution Principle by providing the same interface as the decorated repository.
/// </summary>
/// <typeparam name="TAggregate">The aggregate root type. Must implement <see cref="IAggregate"/>.</typeparam>
/// <typeparam name="TId">The aggregate identifier type. Must be a value type.</typeparam>
/// <typeparam name="TState">The aggregate state type. Must be a struct.</typeparam>
public sealed class SnapshotCachingRepositoryDecorator<TAggregate, TId, TState> : IAggregateRepository<TAggregate, TId>
    where TAggregate : IAggregate
    where TId : struct
    where TState : struct
{
    private readonly IAggregateRepository<TAggregate, TId> _innerRepository;
    private readonly ISnapshotStore<TState> _snapshotStore;
    private readonly SnapshotLoadingStrategy _strategy;
    private readonly Action<TAggregate, TState, StreamPosition> _restoreState;
    private readonly Func<TId, StreamId> _streamIdFactory;
    private readonly IEventStore _eventStore;
    private readonly Func<TAggregate> _aggregateFactory;

    /// <summary>
    /// Initializes the decorator with snapshot loading configuration.
    /// </summary>
    /// <param name="innerRepository">The underlying repository to delegate to.</param>
    /// <param name="snapshotStore">The snapshot store for reading snapshots.</param>
    /// <param name="strategy">The strategy for handling snapshots.</param>
    /// <param name="restoreState">Callback to restore aggregate state from a snapshot before replaying events.</param>
    /// <param name="eventStore">Optional event store for reading events. Required if strategy is not IgnoreSnapshot.</param>
    /// <param name="streamIdFactory">Optional factory to map aggregate ID to stream ID. If not provided, standard convention is used.</param>
    /// <param name="aggregateFactory">Optional factory to create fresh aggregate instances. Must be provided if snapshot optimization is used.</param>
    public SnapshotCachingRepositoryDecorator(
        IAggregateRepository<TAggregate, TId> innerRepository,
        ISnapshotStore<TState> snapshotStore,
        SnapshotLoadingStrategy strategy,
        Action<TAggregate, TState, StreamPosition> restoreState,
        IEventStore? eventStore = null,
        Func<TId, StreamId>? streamIdFactory = null,
        Func<TAggregate>? aggregateFactory = null)
    {
        ArgumentNullException.ThrowIfNull(innerRepository);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(restoreState);

        if (strategy != SnapshotLoadingStrategy.IgnoreSnapshot && eventStore == null)
            throw new ArgumentNullException(nameof(eventStore), "Event store is required when strategy is not IgnoreSnapshot");

        _innerRepository = innerRepository;
        _snapshotStore = snapshotStore;
        _strategy = strategy;
        _restoreState = restoreState;
        _eventStore = eventStore!;
        _streamIdFactory = streamIdFactory ?? (id => new StreamId($"aggregate-{id}"));
        _aggregateFactory = aggregateFactory ?? throw new ArgumentNullException(nameof(aggregateFactory), "Aggregate factory is required for snapshot optimization");
    }

    /// <inheritdoc/>
    public async ValueTask<Result<TAggregate, StoreError>> LoadAsync(TId id, CancellationToken ct = default)
    {
        // If strategy is IgnoreSnapshot, delegate entirely to inner repository
        if (_strategy == SnapshotLoadingStrategy.IgnoreSnapshot)
        {
            return await _innerRepository.LoadAsync(id, ct).ConfigureAwait(false);
        }

        var streamId = _streamIdFactory(id);

        // Try to load snapshot
        var snapshot = await _snapshotStore.ReadAsync(streamId, ct).ConfigureAwait(false);

        // No snapshot found — delegate to inner repository for full load
        if (snapshot == null)
        {
            return await _innerRepository.LoadAsync(id, ct).ConfigureAwait(false);
        }

        var (snapshotPosition, snapshotState) = snapshot.Value;

        // Create a fresh aggregate instance
        var aggregate = _aggregateFactory();

        // Restore the snapshot state
        _restoreState(aggregate, snapshotState, snapshotPosition);

        // For ValidateAndReplay strategy, verify the snapshot position exists in the event store
        if (_strategy == SnapshotLoadingStrategy.ValidateAndReplay)
        {
            // Try to read an event at the snapshot position to validate it exists
            var positionFound = false;
            await foreach (var envelope in _eventStore.ReadAsync(streamId, snapshotPosition, ct).ConfigureAwait(false))
            {
                positionFound = true;
                break;
            }

            // If position not found, fall back to inner repository for full replay
            if (!positionFound)
            {
                return await _innerRepository.LoadAsync(id, ct).ConfigureAwait(false);
            }
        }

        // Replay events from position after the snapshot
        // InMemoryStream.ReadFrom(n) calls Skip(n), so:
        //   ReadFrom(0) returns all events (skip 0)
        //   ReadFrom(1) returns events from index 1 onward (skip 1)
        //   ReadFrom(2) returns events from index 2 onward (skip 2)
        // Since events at indices 0,1,... have positions 1,2,...
        // To replay events after position snapshotPosition, we pass snapshotPosition (which skips snapshotPosition items, starting from index snapshotPosition)
        // This returns events with position > snapshotPosition as desired.
        await foreach (var envelope in _eventStore.ReadAsync(streamId, snapshotPosition, ct).ConfigureAwait(false))
        {
            // Skip events up to and including the snapshot position
            if (envelope.Position.Value <= snapshotPosition.Value)
                continue;
            aggregate.ApplyHistoric(envelope.Event, envelope.Position);
        }

        return Result<TAggregate, StoreError>.Success(aggregate);
    }

    /// <inheritdoc/>
    public async ValueTask<Result<AppendResult, StoreError>> SaveAsync(TAggregate aggregate, TId id, CancellationToken ct = default)
    {
        // SaveAsync is not modified — always delegate to inner repository
        return await _innerRepository.SaveAsync(aggregate, id, ct).ConfigureAwait(false);
    }
}
