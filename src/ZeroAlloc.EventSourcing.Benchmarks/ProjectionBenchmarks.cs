using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Benchmarks for projection operations.
/// Measures performance of projection building and updates.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class ProjectionBenchmarks
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
