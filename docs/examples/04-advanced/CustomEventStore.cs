using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
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
/// The adapter stores <see cref="RawEvent"/>s: the event type name, the serialized payload and the
/// metadata. <see cref="EventStore"/> wraps the adapter and does the serialization and the type
/// lookup. This example keeps the events in a dictionary for simplicity. The SQL Server,
/// PostgreSQL and SQLite adapters that ship as packages follow the same contract against a database.
///
/// This file is compiled and run by the test suite, so it only uses the public API.
/// </summary>
public sealed class DictionaryEventStoreAdapter : IEventStoreAdapter
{
    // In-memory storage: stream ID -> events in stream order
    private readonly Dictionary<string, List<RawEvent>> _streams = new(StringComparer.Ordinal);
    private readonly List<Subscription> _subscriptions = new();
    private readonly object _lock = new();

    /// <summary>
    /// Appends events to a stream with optimistic concurrency.
    /// The stream's version is the number of events in it; a new stream is at
    /// <see cref="StreamPosition.Start"/>. Positions are 1-based: the first event is at 1.
    /// </summary>
    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        RawEvent[] appended;
        Subscription[] subscribers;
        StreamPosition newVersion;

        lock (_lock)
        {
            if (!_streams.TryGetValue(id.Value, out var stream))
            {
                stream = new List<RawEvent>();
                _streams[id.Value] = stream;
            }

            // Check the optimistic lock: the caller must have seen the current version
            var currentVersion = new StreamPosition(stream.Count);
            if (currentVersion != expectedVersion)
            {
                return Result<AppendResult, StoreError>.Failure(
                    StoreError.Conflict(id, expectedVersion, currentVersion));
            }

            // The adapter owns the positions: assign each event the next one
            appended = new RawEvent[events.Length];
            for (var i = 0; i < appended.Length; i++)
            {
                appended[i] = events.Span[i] with { Position = new StreamPosition(stream.Count + 1) };
                stream.Add(appended[i]);
            }

            newVersion = new StreamPosition(stream.Count);

            subscribers = _subscriptions.Where(s => s.StreamId == id && s.IsRunning).ToArray();
        }

        // Notify live subscribers outside the lock
        foreach (var subscriber in subscribers)
        {
            foreach (var e in appended)
                await subscriber.Handler(e, ct);
        }

        return Result<AppendResult, StoreError>.Success(new AppendResult(id, newVersion));
    }

    /// <summary>
    /// Reads the events of a stream that come after <paramref name="from"/>. The bound is
    /// exclusive: <see cref="StreamPosition.Start"/> reads the whole stream, and a consumer that
    /// passes the position of the last event it handled gets only the events after it.
    /// </summary>
    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Copy under the lock, then yield outside it
        List<RawEvent> snapshot;
        lock (_lock)
        {
            snapshot = _streams.TryGetValue(id.Value, out var stream)
                ? stream.Where(e => e.Position.Value > from.Value).ToList()
                : new List<RawEvent>();
        }

        foreach (var e in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    /// <summary>
    /// Subscribes to the events appended to a stream after the subscription starts.
    /// Read the history with <see cref="ReadAsync"/> first if you need it. A database adapter
    /// would poll or use change notifications; the shipped SQL adapters return a
    /// <see cref="PollingEventSubscription"/>.
    /// </summary>
    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        var subscription = new Subscription(this, id, handler);
        lock (_lock)
        {
            _subscriptions.Add(subscription);
        }
        return ValueTask.FromResult<IEventSubscription>(subscription);
    }

    /// <summary>Get all streams (for debugging/testing).</summary>
    public IReadOnlyList<string> GetAllStreams()
    {
        lock (_lock)
        {
            return _streams.Keys.ToList();
        }
    }

    /// <summary>Get event count for a stream.</summary>
    public int GetEventCount(StreamId streamId)
    {
        lock (_lock)
        {
            return _streams.TryGetValue(streamId.Value, out var stream) ? stream.Count : 0;
        }
    }

    private sealed class Subscription(
        DictionaryEventStoreAdapter owner,
        StreamId streamId,
        Func<RawEvent, CancellationToken, ValueTask> handler) : IEventSubscription
    {
        public StreamId StreamId => streamId;
        public Func<RawEvent, CancellationToken, ValueTask> Handler => handler;
        public bool IsRunning { get; private set; }

        // Events are delivered only after StartAsync, so the caller can finish its setup first
        public ValueTask StartAsync(CancellationToken ct = default)
        {
            IsRunning = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            lock (owner._lock)
            {
                owner._subscriptions.Remove(this);
            }
            return ValueTask.CompletedTask;
        }
    }
}

// Events used by the example
public record AccountOpened(string Owner);
public record FundsDeposited(decimal Amount);

/// <summary>
/// Maps event type names to CLR types. For an aggregate, the source generator emits this
/// registry; the example has no aggregate, so it writes one by hand.
/// </summary>
public sealed class ExampleEventTypeRegistry : IEventTypeRegistry
{
    public bool TryGetType(string eventType, out Type? type)
    {
        type = eventType switch
        {
            nameof(AccountOpened) => typeof(AccountOpened),
            nameof(FundsDeposited) => typeof(FundsDeposited),
            _ => null
        };
        return type is not null;
    }

    public string GetTypeName(Type type) => type.Name;
}

/// <summary>
/// Usage example showing how to use a custom event store.
/// </summary>
public static class CustomEventStoreExample
{
    public sealed record Outcome(
        IReadOnlyList<EventEnvelope> Events,
        StoreError? ConflictError,
        IReadOnlyList<object> Delivered,
        int StreamCount);

    public static async Task<Outcome> RunAsync()
    {
        Console.WriteLine("=== Custom Event Store Example ===\n");

        // 1. Create the custom adapter
        var adapter = new DictionaryEventStoreAdapter();

        // 2. Create the event store: it wraps the adapter and adds serialization and type lookup
        var eventStore = new EventStore(adapter, new JsonEventSerializer(), new ExampleEventTypeRegistry());

        // 3. Use the event store like normal
        var streamId = new StreamId("account-123");

        Console.WriteLine("Appending events...");
        var result = await eventStore.AppendAsync(
            streamId,
            new object[] { new AccountOpened("alice"), new FundsDeposited(100m) },
            StreamPosition.Start);

        if (result.IsSuccess)
            Console.WriteLine($"Appended; the stream is now at version {result.Value.NextExpectedVersion.Value}\n");

        // 4. A second writer that still expects a new stream gets a conflict instead of a lost update
        var conflict = await eventStore.AppendAsync(
            streamId,
            new object[] { new FundsDeposited(50m) },
            StreamPosition.Start);
        var conflictError = conflict.IsSuccess ? null : conflict.Error;
        Console.WriteLine($"Stale append: {conflictError}\n");

        // 5. Subscribe to new events, then append one at the current version
        var delivered = new List<object>();
        await using (var subscription = await eventStore.SubscribeAsync(
            streamId,
            StreamPosition.Start,
            (envelope, _) =>
            {
                delivered.Add(envelope.Event);
                return ValueTask.CompletedTask;
            }))
        {
            await subscription.StartAsync();
            await eventStore.AppendAsync(streamId, new object[] { new FundsDeposited(25m) }, result.Value.NextExpectedVersion);
        }

        // 6. Read events back
        Console.WriteLine("Reading events...");
        var events = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAsync(streamId))
        {
            Console.WriteLine($"  Position {envelope.Position.Value}: {envelope.Event}");
            events.Add(envelope);
        }

        // Show internal state
        Console.WriteLine("\nInternal storage:");
        Console.WriteLine($"  Streams: {adapter.GetAllStreams().Count}");
        Console.WriteLine($"  Events in stream: {adapter.GetEventCount(streamId)}");

        return new Outcome(events, conflictError, delivered, adapter.GetAllStreams().Count);
    }

    // A reflection-based JSON serializer keeps the example short. In an application, use the
    // built-in ZeroAllocEventSerializer that AddEventSourcing() registers; it is AOT-safe.
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => JsonSerializer.Deserialize(payload.Span, eventType)!;
    }
}
