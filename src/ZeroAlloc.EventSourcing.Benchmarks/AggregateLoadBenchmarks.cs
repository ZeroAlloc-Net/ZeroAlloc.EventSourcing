using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Benchmarks for aggregate loading performance.
/// Measures performance of loading aggregates from event stores.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class AggregateLoadBenchmarks
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
