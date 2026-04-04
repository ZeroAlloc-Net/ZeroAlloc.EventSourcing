using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Configuration for all benchmarks in the ZeroAlloc.EventSourcing project.
/// Enables memory diagnostics and uses a standard job configuration.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class BenchmarkConfig : ManualConfig
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BenchmarkConfig"/> class.
    /// </summary>
    public BenchmarkConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);

        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationCount(5)
            .AsDefault());
    }
}
