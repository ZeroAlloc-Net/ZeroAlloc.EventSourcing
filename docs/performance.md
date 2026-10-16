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

## Head-to-head vs hand-rolled SQLite event store

<!-- BENCH:START -->
_Last refreshed: 2026-05-14_

Correctness-matched comparison: both rows use the **same SQLite-in-memory connection** (different tables), both check the current stream version inside a transaction before INSERT, both run an ordered SELECT across the stream on read. Storage variance cancels out — the remaining delta is the cost of the `IEventStore` + `IEventStoreAdapter` + `IEventSerializer` + `IEventTypeRegistry` abstraction layer.

To isolate the abstraction overhead from real-world serialization cost (which both sides would pay equally in production), the ZA row uses a cached no-op serializer. The hand-rolled row similarly stores raw bytes without round-tripping JSON.

.NET 8.0.26, i9-12900HK, BenchmarkDotNet v0.15.8.

| Operation | Hand-rolled | ZA.EventSourcing | Overhead |
|---|---:|---:|---:|
| Append (1 event, transactional, OCC check) | 80.7 µs / 3.80 KB | **106.3 µs / 4.79 KB** | +33% time, +26% alloc |
| Read 100-event stream (ordered) | 66.0 µs / 11.95 KB | **140.9 µs / 25.23 KB** | +114% time, +111% alloc |

**Honest reading**: ZA.EventSourcing adds **a measurable abstraction tax** over a raw SQLite event store — about 33% time on append and ~2× on read. That tax buys you:

- Typed events through `IEventStore.AppendAsync<TEvent>` / `ReadAsync` (the hand-rolled version stores raw bytes the caller has to ser/deser themselves)
- Pluggable serialization through `IEventSerializer` (so you can swap System.Text.Json for MessagePack, protobuf, or your own zero-alloc serializer without touching the store)
- Optimistic concurrency through a typed `StreamPosition` API
- Composability with the rest of the ecosystem (`Aggregate<T>`, projections, snapshots, upcasters, dead-letter handling)

In production the dominant cost on both sides is the serializer + the SQL round-trip — at the millisecond scale of a real database both rows converge and this delta becomes invisible.
<!-- BENCH:END -->

## Allocations

What the library itself allocates, measured with allocation-free test doubles for the adapter, serializer and type registry. Each figure is an exact budget asserted by an `AllocationGate` test:

| Call | Library allocation | Test |
|---|---:|---|
| `EventStore.AppendAsync`, 1 to 64 events | 0 B | `EventStoreAllocationTests` |
| `AggregateRepository.SaveAsync` | 0 B | `AggregateRepositoryAllocationTests` |
| `EventStore.ReadAsync`, one full read of 1 or 100 events | 96 B per call, none per event | `EventStoreReadAllocationTests` |

The 96 B is the reader object `ReadAsync` returns, which is also its enumerator. Removing it means returning a struct reader instead of `IAsyncEnumerable<EventEnvelope>`, a breaking change held for the next major ([#512](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/512)). Storage adapters and serializers add their own allocations on top, as the SQLite numbers above show.

## Methodology

Benchmarks are executed using [BenchmarkDotNet](https://benchmarkdotnet.org/) with:
- 3 warmup iterations
- 5 measurement iterations
- Full memory diagnostics enabled
- Nanosecond precision

Benchmarks run before each release to validate no performance regressions have been introduced.
