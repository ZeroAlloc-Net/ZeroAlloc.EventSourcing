# Performance & Benchmarks

Measured 2026-10-17 with BenchmarkDotNet v0.15.8 on an otherwise idle machine, on both of the
library's LTS frameworks, because net9.0 and later compile different metadata and telemetry code:

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9711)
12th Gen Intel Core i9-12900HK 2.50GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.402
  net10.0 : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
  net8.0  : .NET 8.0.32 (8.0.32, 8.0.3226.50405), X64 RyuJIT x86-64-v3
```

> Allocations are exact. Speed is shown as same-run ratios; absolute timings are not published.

Most of these benchmarks measure one operation on its own and have nothing to compare against, so
they publish allocation only. Where a benchmark has a baseline, `Ratio` is each row's mean time
relative to it in the same run, and `Launch range` is the lowest and highest ratio of the three
launches. Source: [`src/ZeroAlloc.EventSourcing.Benchmarks`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/tree/main/src/ZeroAlloc.EventSourcing.Benchmarks).

## Event Store Operations

Appends go through an adapter that stores nothing, so they measure `EventStore` itself: type
mapping, metadata and the hand-off of the raw events. Reads go through the in-memory adapter.

| Operation | net10.0 | net8.0 |
|---|---:|---:|
| Append 1 event | 0 B | 0 B |
| Append 10 events | 0 B | 0 B |
| Read a 100-event stream | 12,216 B | 12,216 B |
| Read 10 streams of 10 events | 21,360 B | 21,360 B |

Read allocations include the in-memory adapter's snapshot of the stream; the library's own share
is the 96 B reader listed under [Allocations](#allocations).

## Snapshot Storage

The in-memory snapshot store.

| Operation | net10.0 | net8.0 |
|---|---:|---:|
| Write a snapshot | 184 B | 184 B |
| Read a snapshot | 0 B | 0 B |
| Write, then read | 192 B | 192 B |

## Aggregate Loading & Replay

Loading an aggregate by replaying its stream from the in-memory adapter, with the System.Text.Json
serializer, so each event is deserialized.

| Stream length | net10.0 | net8.0 |
|---|---:|---:|
| 10 events | 4,392 B | 4,472 B |
| 100 events | 32,472 B | 33,272 B |
| 1,000 events | 313,272 B | 321,272 B |

About 310 B per replayed event.

## Projection Application

| Operation | net10.0 | net8.0 |
|---|---:|---:|
| Apply one event | 112 B | 112 B |
| Apply three events, dynamic dispatch | 224 B | 224 B |
| Apply three events, typed dispatch | 224 B | 224 B |

## Serialization

The System.Text.Json reflection serializer against `ZeroAllocEventSerializer` over a hand-wired
dispatcher, serializing and deserializing one small event. The baseline is reflection serialize.

| Method | net10.0 Ratio | net10.0 Launch range | net8.0 Ratio | net8.0 Launch range | Allocated |
|---|---:|---:|---:|---:|---:|
| Reflection_Serialize | 1.00 | | 1.00 | | 64 B |
| Dispatcher_Serialize | 1.01 | 0.99–1.03 | 1.03 | 1.02–1.06 | 64 B |
| Reflection_Deserialize | 1.88 | 1.82–1.93 | 1.75 | 1.74–1.75 | 208 B, 216 B on net8.0 |
| Dispatcher_Deserialize | 1.97 | 1.89–2.02 | 1.72 | 1.69–1.74 | 208 B, 216 B on net8.0 |

Once System.Text.Json has cached its reflection metadata, both serializers cost the same within a
few percent and allocate the same. The dispatcher's value is that it needs no reflection, so it
works under NativeAOT and trimming.

## Stream consumers

`StreamConsumer` reading 10,000 events from the global stream of the in-memory adapter, with a
handler that does nothing.

| Variant | net10.0 | net8.0 |
|---|---:|---:|
| Batch size 100 (default) | 3,427,360 B | 3,507,546 B |
| Batch size 10 | 3,836,261 B | 3,916,178 B |
| Batch size 1,000 | 3,570,600 B | 3,650,584 B |
| Commit after every event | 3,902,568 B | 3,982,754 B |
| Commit after every batch | 3,427,368 B | 3,507,384 B |
| Resume from a checkpoint | 3,428,328 B | 3,508,264 B |
| Read the position after consuming | 3,427,464 B | 3,507,618 B |

About 350 B per event, including the in-memory adapter's copy of each batch. Until
[#547](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/547) was fixed the adapter
re-read and sorted the whole store for every batch, so these figures were 245 MB per run at batch
size 100 and 2.4 GB at batch size 10.

## Kafka consumer

`KafkaManualPartitionConsumer` consuming N messages from a fake consumer that replays pre-built
messages, with a handler that does nothing. Every measured call consumes all N.

| Variant | N | net10.0 | net8.0 |
|---|---:|---:|---:|
| Batch size 100, commit after batch | 100 | 47,536 B | 82,744 B |
| | 1,000 | 466,360 B | 818,368 B |
| | 10,000 | 4,654,600 B | 8,174,608 B |
| Batch size 10, commit after batch | 10,000 | 5,073,001 B | 8,593,013 B |
| Batch size 100, commit after each event | 10,000 | 5,209,000 B | 8,729,013 B |
| Map one message to an `EventEnvelope` | 1 | 400 B | 752 B |

About 465 B per message on net10.0 and 817 B on net8.0. The library code is the same on both; the
difference is in mapping a message to an envelope, which allocates 752 B per message on the .NET 8
runtime against 400 B on .NET 10.

## Head-to-head vs hand-rolled SQLite event store

<!-- BENCH:START -->
_Last refreshed: 2026-10-17_

Correctness-matched comparison: both rows use the **same SQLite-in-memory connection** (different tables), both check the current stream version inside a transaction before INSERT, both run an ordered SELECT across the stream on read. Storage variance cancels out — the remaining delta is the cost of the `IEventStore` + `IEventStoreAdapter` + `IEventSerializer` + `IEventTypeRegistry` abstraction layer.

To isolate the abstraction overhead from real-world serialization cost (which both sides would pay equally in production), the ZA row uses a cached no-op serializer. The hand-rolled row similarly stores raw bytes without round-tripping JSON.

Each ZA row's ratio is against the hand-rolled row of the same operation in the same run.

| Operation | Framework | Hand-rolled | ZA.EventSourcing | Launch range | Allocated, hand-rolled → ZA |
|---|---|---:|---:|---:|---:|
| Append 1 event (transactional, OCC check) | net10.0 | 1.00 | **1.20** | 1.06–1.35 | 3,888 B → 4,688 B |
| | net8.0 | 1.00 | **1.30** | 1.26–1.37 | 3,888 B → 4,680 B |
| Read 100-event stream (ordered) | net10.0 | 1.00 | **2.03** | 1.99–2.04 | 12,232 B → 25,632 B |
| | net8.0 | 1.00 | **2.10** | 2.07–2.13 | 12,232 B → 25,632 B |

**Honest reading**: ZA.EventSourcing adds **a measurable abstraction tax** over a raw SQLite event store — 20–30% time on append and about 2× on read. That tax buys you:

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

Each benchmark ran out of process in three launches of 6 warmup and 15 measured iterations, pinned
to two performance cores. To reproduce on one framework:

```bash
dotnet run -c Release --project src/ZeroAlloc.EventSourcing.Benchmarks -f net10.0 -- \
  --filter "*" --launchCount 3 --warmupCount 6 --iterationCount 15 --affinity 20
```

The benchmark classes declare short jobs so the manual CI run stays inside its time limit; the
counts on the command line override them.
