using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Test aggregate state for benchmarking.
/// Represents an order with basic properties for snapshot storage.
/// </summary>
internal struct BenchmarkOrderState
{
    public bool IsPlaced { get; set; }
    public bool IsShipped { get; set; }
    public decimal Total { get; set; }
    public string? OrderId { get; set; }
    public string? TrackingNumber { get; set; }
}

/// <summary>
/// Benchmarks for SnapshotStore operations.
/// Measures performance of snapshot storage and retrieval.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
public class SnapshotStoreBenchmarks
{
    private const int BaseOrderId = 100;

    private InMemorySnapshotStore<BenchmarkOrderState> _snapshotStore = null!;
    private BenchmarkOrderState _state;
    private StreamId _streamId;
    private StreamPosition _position;
    private int _snapshotCounter;

    /// <summary>
    /// Global setup for benchmarks.
    /// Initializes the snapshot store and pre-populates with test BenchmarkOrderState.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _snapshotStore = new InMemorySnapshotStore<BenchmarkOrderState>();
        _streamId = new StreamId("order-benchmark");
        _position = new StreamPosition(50);
        _state = new BenchmarkOrderState
        {
            IsPlaced = true,
            IsShipped = false,
            Total = 1000m,
            OrderId = "ORD-001",
            TrackingNumber = null
        };
        _snapshotCounter = 0;

        // Pre-populate for read benchmarks
        _snapshotStore.WriteAsync(_streamId, _position, _state, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Benchmark: WriteSnapshot - measure writing a single snapshot.
    /// Creates a new stream per invocation to avoid conflicts and measure pure write performance.
    /// </summary>
    [Benchmark]
    public async Task WriteSnapshot()
    {
        var newState = new BenchmarkOrderState
        {
            IsPlaced = true,
            IsShipped = false,
            Total = 2000m,
            OrderId = $"ORD-{BaseOrderId + _snapshotCounter:000}",
            TrackingNumber = null
        };

        var streamId = new StreamId($"order-write-{_snapshotCounter}");
        await _snapshotStore.WriteAsync(
            streamId,
            new StreamPosition(75),
            newState,
            CancellationToken.None
        );

        _snapshotCounter++;
    }

    /// <summary>
    /// Benchmark: ReadSnapshot - measure reading a snapshot.
    /// Reads from the pre-populated stream to measure pure read performance.
    /// </summary>
    [Benchmark]
    public async Task ReadSnapshot()
    {
        var result = await _snapshotStore.ReadAsync(_streamId, CancellationToken.None);

        if (!result.HasValue)
        {
            throw new InvalidOperationException(
                $"Failed to read snapshot from stream {_streamId}");
        }
    }

    /// <summary>
    /// Benchmark: WriteAndReadSnapshot - measure round-trip write and read performance.
    /// Creates a new snapshot, writes it, then reads it back to measure full round-trip.
    /// </summary>
    [Benchmark]
    public async Task WriteAndReadSnapshot()
    {
        var newState = new BenchmarkOrderState
        {
            IsPlaced = true,
            IsShipped = false,
            Total = 3000m,
            OrderId = $"ORD-{BaseOrderId + _snapshotCounter:000}",
            TrackingNumber = null
        };

        var streamId = new StreamId($"order-roundtrip-{_snapshotCounter}");
        await _snapshotStore.WriteAsync(
            streamId,
            new StreamPosition(100),
            newState,
            CancellationToken.None
        );

        var readResult = await _snapshotStore.ReadAsync(streamId, CancellationToken.None);

        if (!readResult.HasValue)
        {
            throw new InvalidOperationException(
                $"Failed to read snapshot during round-trip for stream {streamId}");
        }

        _snapshotCounter++;
    }
}
