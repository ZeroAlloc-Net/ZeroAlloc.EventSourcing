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
    private const int PrePopulationCount = 100;

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

        // Pre-populate single stream for ReadEventsFromStream benchmark
        for (int i = 0; i < PrePopulationCount; i++)
        {
            var @event = new BenchmarkEvent($"event-{i}", new byte[] { 1, 2, 3, 4, 5 });
            var result = _eventStore.AppendAsync(
                _streamId,
                new object[] { @event }.AsMemory(),
                new StreamPosition(_eventCount),
                CancellationToken.None
            ).GetAwaiter().GetResult();

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Failed to append event {i} to benchmark stream during setup: {result.Error}");
            }

            _eventCount++;
        }

        // Pre-populate 10 separate streams for ReadEventsAcrossStreams benchmark
        for (int streamIdx = 0; streamIdx < 10; streamIdx++)
        {
            var streamId = new StreamId($"stream-{streamIdx}");
            for (int eventIdx = 0; eventIdx < 10; eventIdx++)
            {
                var @event = new BenchmarkEvent($"stream-{streamIdx}-event-{eventIdx}", new byte[] { 1, 2, 3, 4, 5 });
                var result = _eventStore.AppendAsync(
                    streamId,
                    new object[] { @event }.AsMemory(),
                    new StreamPosition(eventIdx),
                    CancellationToken.None
                ).GetAwaiter().GetResult();

                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Failed to append event {eventIdx} to stream-{streamIdx} during setup: {result.Error}");
                }
            }
        }
    }

    /// <summary>
    /// Benchmark: AppendEvent - measure appending a single event to a stream.
    /// Creates a new stream per invocation to avoid conflicts between benchmark invocations.
    /// Each invocation increments _eventCount to ensure unique stream identifiers.
    /// </summary>
    [Benchmark]
    public async Task AppendEvent()
    {
        var @event = new BenchmarkEvent($"event-{_eventCount}", new byte[] { 1, 2, 3, 4, 5 });
        var result = await _eventStore.AppendAsync(
            new StreamId($"stream-{_eventCount}"),
            new object[] { @event }.AsMemory(),
            StreamPosition.Start,
            CancellationToken.None
        );

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to append event to stream-{_eventCount}: {result.Error}");
        }

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
    /// Reads from 10 pre-populated streams (100 events total: 10 streams × 10 events).
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

        // Verify we read all expected events (10 streams × 10 events each)
        if (allEvents != 100)
        {
            throw new InvalidOperationException(
                $"Expected to read 100 events across 10 streams, but read {allEvents}");
        }
    }
}
