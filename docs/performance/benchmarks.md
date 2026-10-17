# Benchmark Results

The measured results, the environment and the command to reproduce them are on one page:
**[Performance & Benchmarks](../performance.md)**.

That page publishes exact allocations for every benchmark on net8.0 and net10.0, and same-run
ratios where a benchmark has a baseline. It does not publish absolute timings: they depend on the
machine and drift between runs, while allocations are exact and a ratio of two benchmarks measured
side by side holds.

This page used to carry its own copy of an earlier run, with absolute timings and throughput
estimates derived from them. That copy had drifted from the benchmarks, so it was removed rather
than kept in step with the main page.

## Related

- **[Performance Characteristics](./characteristics.md)** — where the library allocates and why
- **[Zero-Allocation Design](./zero-allocation.md)** — the design philosophy
- **[Optimization Strategies](./optimization.md)** — practical tips for production workloads
