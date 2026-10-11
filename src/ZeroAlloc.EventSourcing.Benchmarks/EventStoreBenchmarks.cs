using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Results;

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
/// System.Text.Json reflection serializer. <see cref="SerializerBenchmarks"/> compares it with the
/// dispatcher serializer; the event store benchmarks use <see cref="CachedSerializer"/> instead, so
/// serialization cost does not hide the overhead of the library.
/// </summary>
internal sealed class BenchmarkSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

/// <summary>
/// An adapter that stores nothing and completes every append synchronously. Appending through it
/// measures what <see cref="EventStore"/> itself costs: type mapping, metadata and the hand-off
/// of the raw events.
/// </summary>
internal sealed class NoOpEventStoreAdapter : IEventStoreAdapter
{
    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
        => ValueTask.FromResult(Result<AppendResult, StoreError>.Success(
            new AppendResult(id, new StreamPosition(expectedVersion.Value + events.Length))));

    public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        => throw new NotSupportedException();

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Benchmarks for <see cref="EventStore"/>. Serialization goes through <see cref="CachedSerializer"/>
/// and every input is built in setup, so the numbers are the library's own: an append through
/// <see cref="NoOpEventStoreAdapter"/> allocates nothing.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class EventStoreBenchmarks
{
    private const int PrePopulationCount = 100;
    private const int StreamCount = 10;
    private const int EventsPerStream = 10;

    private IEventStore _appendStore = null!;
    private IEventStore _readStore = null!;
    private readonly StreamId _appendStream = new("append-stream");
    private readonly StreamId _readStream = new("benchmark-stream");
    private readonly StreamId[] _streams = new StreamId[StreamCount];
    private ReadOnlyMemory<object> _oneEvent;
    private ReadOnlyMemory<object> _tenEvents;

    /// <summary>
    /// Builds the append inputs, and pre-populates an in-memory store with one 100-event stream
    /// and ten 10-event streams for the read benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var registry = new BenchmarkTypeRegistry();
        var serializer = new CachedSerializer();
        _appendStore = new EventStore(new NoOpEventStoreAdapter(), serializer, registry);
        _readStore = new EventStore(new InMemoryEventStoreAdapter(), serializer, registry);

        var @event = new BenchmarkEvent("event", new byte[] { 1, 2, 3, 4, 5 });
        _oneEvent = new object[] { @event };
        _tenEvents = Enumerable.Repeat<object>(@event, 10).ToArray();

        for (var i = 0; i < PrePopulationCount; i++)
            Append(_readStore, _readStream, i, _oneEvent);

        for (var s = 0; s < StreamCount; s++)
        {
            _streams[s] = new StreamId($"stream-{s}");
            for (var i = 0; i < EventsPerStream; i++)
                Append(_readStore, _streams[s], i, _oneEvent);
        }
    }

    /// <summary>Append one event: the cost of <see cref="EventStore"/> on top of the adapter.</summary>
    [Benchmark]
    public async ValueTask<long> AppendEvent()
    {
        var result = await _appendStore.AppendAsync(_appendStream, _oneEvent, StreamPosition.Start, CancellationToken.None);
        return result.Value.NextExpectedVersion.Value;
    }

    /// <summary>Append ten events in one call: the per-event cost of <see cref="EventStore"/>.</summary>
    [Benchmark]
    public async ValueTask<long> AppendTenEvents()
    {
        var result = await _appendStore.AppendAsync(_appendStream, _tenEvents, StreamPosition.Start, CancellationToken.None);
        return result.Value.NextExpectedVersion.Value;
    }

    /// <summary>Read all 100 events of one in-memory stream.</summary>
    [Benchmark]
    public async ValueTask<int> ReadEventsFromStream()
    {
        var count = 0;
        await foreach (var _ in _readStore.ReadAsync(_readStream, StreamPosition.Start, CancellationToken.None))
            count++;
        return count;
    }

    /// <summary>Read ten in-memory streams of ten events each.</summary>
    [Benchmark]
    public async ValueTask<int> ReadEventsAcrossStreams()
    {
        var count = 0;
        foreach (var stream in _streams)
        {
            await foreach (var _ in _readStore.ReadAsync(stream, StreamPosition.Start, CancellationToken.None))
                count++;
        }

        return count;
    }

    private static void Append(IEventStore store, StreamId stream, long expectedVersion, ReadOnlyMemory<object> events)
    {
        var result = store.AppendAsync(stream, events, new StreamPosition(expectedVersion), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Failed to append to {stream.Value} during setup: {result.Error}");
    }
}
