using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Examples.Advanced;

/// <summary>
/// This example demonstrates how to implement a custom event store adapter.
///
/// A custom event store adapter allows you to:
/// - Persist events to any storage backend (SQL, MongoDB, RavenDB, etc.)
/// - Use your existing database infrastructure
/// - Optimize for your specific access patterns
///
/// This example uses an in-memory dictionary for simplicity,
/// but demonstrates the patterns for a real SQL store.
/// </summary>

public class InMemoryEventStoreAdapter : IEventStoreAdapter
{
    // In-memory storage: stream ID -> list of events
    private readonly Dictionary<string, List<SerializedEvent>> _streams = new();
    private readonly object _lock = new();

    /// <summary>
    /// Appends events to a stream with optimistic locking.
    /// </summary>
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId streamId,
        ReadOnlyMemory<SerializedEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Validate inputs
            if (events.Length == 0)
                return Result.Error<AppendResult, StoreError>(
                    StoreError.Other("No events to append"));

            var key = streamId.Value;

            // Get current stream (or create new)
            if (!_streams.ContainsKey(key))
                _streams[key] = new List<SerializedEvent>();

            var stream = _streams[key];

            // Check optimistic lock: expected version must match current position
            // Current position is the last event's position (or -1 if empty)
            var currentPosition = new StreamPosition(stream.Count - 1);

            if (currentPosition != expectedVersion)
            {
                return Result.Error<AppendResult, StoreError>(
                    StoreError.Conflict(
                        "Expected version {0}, but current position is {1}",
                        expectedVersion.Value,
                        currentPosition.Value));
            }

            // Append events
            var firstPosition = new StreamPosition(stream.Count);
            var nextPosition = firstPosition;

            foreach (var @event in events.Span)
            {
                stream.Add(@event);
                nextPosition = new StreamPosition(stream.Count - 1);
            }

            return Result.Ok<AppendResult, StoreError>(
                new AppendResult(firstPosition, nextPosition));
        }
    }

    /// <summary>
    /// Reads events from a stream starting at the given position.
    /// </summary>
    public async IAsyncEnumerable<SerializedEvent> ReadAsync(
        StreamId streamId,
        StreamPosition from = default,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        lock (_lock)
        {
            var key = streamId.Value;

            if (!_streams.TryGetValue(key, out var stream))
                yield break;

            var startIndex = from.Value + 1;
            if (startIndex < 0)
                startIndex = 0;

            for (int i = startIndex; i < stream.Count; i++)
            {
                var @event = stream[i];
                // Add position information for output
                yield return new SerializedEvent(
                    @event.Type,
                    @event.Data,
                    new StreamPosition(i));
            }
        }
    }

    /// <summary>
    /// Subscribes to events appended to a stream.
    /// </summary>
    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId streamId,
        StreamPosition from,
        Func<SerializedEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        // 1. Read all existing events
        await foreach (var @event in ReadAsync(streamId, from, ct))
        {
            await handler(@event, ct);
        }

        // 2. For this in-memory store, we can't really subscribe to future events
        // In a real store, you'd use database notifications or message queues
        return new InMemorySubscription();
    }

    private class InMemorySubscription : IEventSubscription
    {
        public async ValueTask DisposeAsync()
        {
            // Nothing to cleanup
        }
    }

    /// <summary>
    /// Get all streams (for debugging/testing).
    /// </summary>
    public IEnumerable<string> GetAllStreams()
    {
        lock (_lock)
        {
            return _streams.Keys.ToList();
        }
    }

    /// <summary>
    /// Get event count for a stream.
    /// </summary>
    public int GetEventCount(StreamId streamId)
    {
        lock (_lock)
        {
            return _streams.TryGetValue(streamId.Value, out var stream)
                ? stream.Count
                : 0;
        }
    }

    /// <summary>
    /// Clear all data (for testing).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _streams.Clear();
        }
    }
}

/// <summary>
/// Example: How you would implement with SQL Server.
/// (Pseudo-code, not fully functional)
/// </summary>
public class SqlServerEventStoreAdapterExample : IEventStoreAdapter
{
    // This is how you'd structure a SQL implementation:
    // 1. Use ADO.NET or a library like Dapper
    // 2. Store events in a table: Events (StreamId, Position, EventType, EventData, Timestamp)
    // 3. Use transactions for optimistic locking via unique constraint (StreamId, Position)
    // 4. Query from the Events table for reads

    private readonly string _connectionString;

    public SqlServerEventStoreAdapterExample(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId streamId,
        ReadOnlyMemory<SerializedEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        // 1. Open connection
        // 2. Begin transaction (SERIALIZABLE for safety)
        // 3. SELECT MAX(Position) FROM Events WHERE StreamId = @StreamId
        // 4. If result != expectedVersion, rollback and return Conflict
        // 5. INSERT INTO Events (StreamId, Position, EventType, EventData, Timestamp)
        //    VALUES (@StreamId, @Position, @EventType, @EventData, @Timestamp) x N
        // 6. Commit transaction
        // 7. Return positions

        throw new NotImplementedException("See SqlServerEventStoreAdapter in custom-event-store.md");
    }

    public async IAsyncEnumerable<SerializedEvent> ReadAsync(
        StreamId streamId,
        StreamPosition from = default,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1. Open connection
        // 2. SELECT EventType, EventData, Position FROM Events
        //    WHERE StreamId = @StreamId AND Position >= @FromPosition
        //    ORDER BY Position ASC
        // 3. For each row, yield SerializedEvent

        throw new NotImplementedException("See SqlServerEventStoreAdapter in custom-event-store.md");
    }

    public async ValueTask<IEventSubscription> SubscribeAsync(
        StreamId streamId,
        StreamPosition from,
        Func<SerializedEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        // 1. Read all existing events using ReadAsync
        // 2. Set up SQL Server Service Broker or polling for new events
        // 3. Call handler for each new event

        throw new NotImplementedException("See SqlServerEventStoreAdapter in custom-event-store.md");
    }
}

/// <summary>
/// Usage example showing how to use a custom event store.
/// </summary>
public class CustomEventStoreExample
{
    public static async Task Main()
    {
        Console.WriteLine("=== Custom Event Store Example ===\n");

        // 1. Create the custom adapter
        var adapter = new InMemoryEventStoreAdapter();

        // 2. Create the event store (IEventStore wraps the adapter)
        var eventStore = new EventStore(adapter, new JsonEventSerializer());

        // 3. Use the event store like normal
        var streamId = new StreamId("order-123");

        // Append some events
        var events = new object[]
        {
            new { type = "OrderPlaced", total = 1000m },
            new { type = "OrderConfirmed" },
        };

        Console.WriteLine("Appending events...");
        var result = await eventStore.AppendAsync(
            streamId,
            events,
            StreamPosition.Start);

        if (result.IsSuccess)
        {
            Console.WriteLine($"✓ Appended events, positions {result.Value.FirstPosition} to {result.Value.LastPosition}\n");
        }

        // Read events back
        Console.WriteLine("Reading events...");
        await foreach (var envelope in eventStore.ReadAsync(streamId))
        {
            Console.WriteLine($"  Position {envelope.Position}: {envelope.Event}");
        }

        // Show internal state
        Console.WriteLine($"\nInternal storage:");
        Console.WriteLine($"  Streams: {adapter.GetAllStreams().Count()}");
        Console.WriteLine($"  Events in stream: {adapter.GetEventCount(streamId)}");
    }
}
