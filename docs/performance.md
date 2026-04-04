# Performance & Benchmarks

**Version:** Next Release  
**Last Updated:** 2026-04-04

## Event Store Operations

Benchmarks for core event store functionality: appending events and reading event streams.

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| AppendEvent | — | — | — | — |
| ReadEventsFromStream | — | — | — | — |
| ReadEventsAcrossStreams | — | — | — | — |

## Snapshot Storage

Benchmarks for snapshot read/write operations (InMemory implementation).

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| WriteSnapshot | — | — | — | — |
| ReadSnapshot | — | — | — | — |
| WriteAndReadSnapshot | — | — | — | — |

## Aggregate Loading & Replay

Benchmarks for loading aggregates from event streams of varying sizes.

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| LoadAggregateSmallStream (10 events) | — | — | — | — |
| LoadAggregateMediumStream (100 events) | — | — | — | — |
| LoadAggregateLargeStream (1000 events) | — | — | — | — |

## Projection Application

Benchmarks for applying events to projections (read models).

| Operation | Mean (ns) | Allocations | P95 (ns) | P99 (ns) |
|-----------|-----------|-------------|----------|----------|
| ApplySingleEvent | — | — | — | — |
| ApplyMultipleEventsSequential | — | — | — | — |
| ApplyTypedDispatch | — | — | — | — |

## Zero-Allocation Compliance

All core operations target **zero allocations** in steady state. Any allocations shown above represent transient state during benchmark execution and are acceptable for this zero-allocation library.

## Methodology

Benchmarks are executed using [BenchmarkDotNet](https://benchmarkdotnet.org/) with:
- 3 warmup iterations
- 5 measurement iterations
- Full memory diagnostics enabled
- Nanosecond precision

Benchmarks run before each release to validate no performance regressions have been introduced.
