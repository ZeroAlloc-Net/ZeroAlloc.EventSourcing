using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace ZeroAlloc.EventSourcing.Benchmarks;

// A no-op serializer that returns a pre-cached byte array on Serialize and a
// pre-cached object on Deserialize. Used by HandRolledOverheadBenchmark so the
// measured delta isolates the ZA.EventSourcing abstraction overhead from the
// dominating cost of real-world JSON serialization (which both sides would pay
// equally in production). Matches the FakeSerializer pattern used by the
// ZA.Outbox benchmark.
internal sealed class CachedSerializer : IEventSerializer
{
    private static readonly byte[] s_payload = new byte[32];
    private static readonly BenchmarkEvent s_event = new("cached", s_payload);

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull => s_payload;
    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType) => s_event;
}

// Measures the overhead ZA.EventSourcing adds on top of a correctness-matched
// hand-rolled SQLite event store: per-append optimistic-concurrency check inside
// a transaction, ordered read across a stream. Both rows use the same SQLite
// in-memory connection (different tables) so storage variance cancels out — the
// delta is the cost of the IEventStore + IEventStoreAdapter + IEventSerializer
// + IEventTypeRegistry abstraction layer.

/// <summary>Benchmarks ZA.EventSourcing overhead vs a correctness-matched hand-rolled SQLite event store.</summary>
[MemoryDiagnoser]
[SimpleJob]
public class HandRolledOverheadBenchmark
{
    private const int StreamLength = 100;

    private SqliteConnection _conn = null!;
    private HandRolledSqliteEventStore _hand = null!;
    private SqliteEventStoreAdapter _zaAdapter = null!;
    private EventStore _zaStore = null!;

    private readonly BenchmarkEvent _appendEvent = new("evt-x", new byte[32]);
    private readonly byte[] _handPayload = new byte[32];
    private readonly StreamId _readStream = new("read-stream");
    private long _appendVersion;
    private long _appendVersionZa;

    /// <summary>Opens the shared SQLite-in-memory connection and pre-populates the read-stream rows.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        _hand = new HandRolledSqliteEventStore(_conn);
        _zaAdapter = new SqliteEventStoreAdapter(_conn);
        var registry = new BenchmarkTypeRegistry();
        var serializer = new CachedSerializer();
        _zaStore = new EventStore(_zaAdapter, serializer, registry);

        // Pre-populate the read-streams with 100 events each. The payload is the
        // same fixed byte array used by the cached serializer so both sides see
        // identical storage cost.
        var payload = new byte[32];
        for (var i = 0; i < StreamLength; i++)
        {
            _hand.Append("read-stream", i, nameof(BenchmarkEvent), payload);
        }
        for (var i = 0; i < StreamLength; i++)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO za_events (stream_id, position, event_type, payload, event_id, occurred_at)
                VALUES ('read-stream', $pos, 'BenchmarkEvent', $payload, $eid, $occurred);
                """;
            cmd.Parameters.AddWithValue("$pos", i + 1);
            cmd.Parameters.AddWithValue("$payload", payload);
            cmd.Parameters.AddWithValue("$eid", Guid.NewGuid().ToByteArray());
            cmd.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Disposes the shared SQLite connection.</summary>
    [GlobalCleanup]
    public void Cleanup() => _conn.Dispose();

    // --- Append (single event) ---
    //
    // Each iteration appends one event to a per-row append stream — we reset
    // version counters and clear the append rows between iterations so each
    // run starts from a known state.

    /// <summary>Resets the append-stream rows and version counters before each append iteration.</summary>
    [IterationSetup(Targets = [nameof(Hand_Append), nameof(Za_Append)])]
    public void PrepareAppend()
    {
        _appendVersion = 0;
        _appendVersionZa = 0;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM hand_events WHERE stream_id = 'append-stream';
            DELETE FROM za_events WHERE stream_id = 'append-stream';
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Hand-rolled baseline: append one event via raw SQLite.</summary>
    [Benchmark(Baseline = true, Description = "Hand-rolled: append 1 event")]
    [BenchmarkCategory("Append1")]
    public long Hand_Append()
    {
        _appendVersion = _hand.Append("append-stream", _appendVersion, nameof(BenchmarkEvent), _handPayload);
        return _appendVersion;
    }

    /// <summary>ZA.EventSourcing: append one event through the full IEventStore + adapter + serializer pipeline.</summary>
    [Benchmark(Description = "ZA.EventSourcing: append 1 event")]
    [BenchmarkCategory("Append1")]
    public async ValueTask<long> Za_Append()
    {
        var result = await _zaStore.AppendAsync(
            new StreamId("append-stream"),
            new object[] { _appendEvent }.AsMemory(),
            new StreamPosition(_appendVersionZa),
            CancellationToken.None);
        if (!result.IsSuccess)
            throw new InvalidOperationException("append failed: " + result.Error);
        _appendVersionZa = result.Value.NextExpectedVersion.Value;
        return _appendVersionZa;
    }

    // --- Read (100-event stream) ---
    //
    // No per-iteration reset needed — read is idempotent and the stream is
    // pre-populated in GlobalSetup.

    /// <summary>Hand-rolled baseline: read a 100-event stream via raw SQLite.</summary>
    [Benchmark(Description = "Hand-rolled: read 100-event stream")]
    [BenchmarkCategory("Read100")]
    public int Hand_Read() => _hand.ReadAll("read-stream", static (_, _, _) => { });

    /// <summary>ZA.EventSourcing: read a 100-event stream through the full IEventStore + adapter + serializer pipeline.</summary>
    [Benchmark(Description = "ZA.EventSourcing: read 100-event stream")]
    [BenchmarkCategory("Read100")]
    public async ValueTask<int> Za_Read()
    {
        var count = 0;
        await foreach (var _ in _zaStore.ReadAsync(_readStream, StreamPosition.Start, CancellationToken.None))
            count++;
        return count;
    }
}
