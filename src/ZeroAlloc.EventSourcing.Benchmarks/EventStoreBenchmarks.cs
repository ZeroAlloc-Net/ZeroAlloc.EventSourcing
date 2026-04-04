using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Benchmarks for EventStore operations.
/// Measures performance of event storage and retrieval.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class EventStoreBenchmarks
{
    /// <summary>
    /// Placeholder benchmark - to be implemented.
    /// </summary>
    [Benchmark]
    public void Placeholder()
    {
        // Placeholder benchmark - to be implemented
    }
}
