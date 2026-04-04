using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Test event for benchmarking.
/// </summary>
internal record BenchmarkEvent(string Id, byte[] Payload);

/// <summary>
/// Minimal type registry for benchmarks.
/// </summary>
internal sealed class BenchmarkTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _map = new()
    {
        [nameof(BenchmarkEvent)] = typeof(BenchmarkEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => _map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}

/// <summary>
/// Minimal JSON serializer for benchmarks.
/// </summary>
internal sealed class BenchmarkSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

/// <summary>
/// Benchmarks for EventStore operations.
/// Measures performance of event storage and retrieval.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
public class EventStoreBenchmarks
{
    private IEventStore _eventStore = null!;
    private StreamId _streamId;
    private int _eventCount;

    /// <summary>
    /// Global setup for benchmarks.
    /// Pre-populates the event store with 100 events for read benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var registry = new BenchmarkTypeRegistry();
        var serializer = new BenchmarkSerializer();
        _eventStore = new EventStore(adapter, serializer, registry);

        _streamId = new StreamId("benchmark-stream");
        _eventCount = 0;

        // Pre-populate with 100 events for read benchmarks
        for (int i = 0; i < 100; i++)
        {
            var @event = new BenchmarkEvent($"event-{i}", new byte[] { 1, 2, 3, 4, 5 });
            _eventStore.AppendAsync(
                _streamId,
                new object[] { @event }.AsMemory(),
                new StreamPosition(_eventCount),
                CancellationToken.None
            ).GetAwaiter().GetResult();
            _eventCount++;
        }
    }

    /// <summary>
    /// Benchmark: AppendEvent - measure appending a single event to a stream.
    /// </summary>
    [Benchmark]
    public async Task AppendEvent()
    {
        var @event = new BenchmarkEvent($"event-{_eventCount}", new byte[] { 1, 2, 3, 4, 5 });
        await _eventStore.AppendAsync(
            new StreamId($"stream-{_eventCount}"),
            new object[] { @event }.AsMemory(),
            StreamPosition.Start,
            CancellationToken.None
        );
        _eventCount++;
    }

    /// <summary>
    /// Benchmark: ReadEventsFromStream - measure reading all events from a single stream.
    /// </summary>
    [Benchmark]
    public async Task ReadEventsFromStream()
    {
        var events = new List<EventEnvelope>();
        await foreach (var evt in _eventStore.ReadAsync(_streamId, StreamPosition.Start, CancellationToken.None))
        {
            events.Add(evt);
        }
    }

    /// <summary>
    /// Benchmark: ReadEventsAcrossStreams - measure reading from multiple streams.
    /// </summary>
    [Benchmark]
    public async Task ReadEventsAcrossStreams()
    {
        var allEvents = 0;
        for (int i = 0; i < 10; i++)
        {
            await foreach (var evt in _eventStore.ReadAsync(
                new StreamId($"stream-{i}"),
                StreamPosition.Start,
                CancellationToken.None
            ))
            {
                allEvents++;
            }
        }
    }
}
