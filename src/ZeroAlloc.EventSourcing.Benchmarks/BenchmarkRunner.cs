using BenchmarkDotNet.Running;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Main entry point for running all benchmarks.
/// </summary>
internal class BenchmarkRunner
{
    /// <summary>
    /// Main method - entry point for the benchmark application.
    /// </summary>
    /// <param name="args">Command line arguments passed to BenchmarkDotNet.</param>
    static void Main(string[] args)
    {
        var summary = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkRunner).Assembly)
            .Run(args);
    }
}
