# ZeroAlloc.EventSourcing

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.EventSourcing.svg)](https://www.nuget.org/packages/ZeroAlloc.EventSourcing)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

Event sourcing for .NET with source-generated aggregates: appends allocate nothing of their own, there is no reflection, and it runs under NativeAOT.

## Why ZeroAlloc.EventSourcing

- **No allocation on the write path.** `EventStore.AppendAsync` and `AggregateRepository.SaveAsync` allocate 0 B of their own, asserted by exact-budget tests. A full stream read costs 96 B per call and nothing per event.
- **Works under NativeAOT and trimming.** Aggregate event dispatch and the event type registry are generated at compile time. CI publishes and runs an AOT app for the core and for every storage adapter.
- **Compile-time diagnostics.** The generator tells you when an aggregate or projection cannot be generated, instead of it silently doing nothing at runtime.
- **Tested against real databases.** SQL Server and PostgreSQL run through Testcontainers, SQLite in-process, with optimistic concurrency checked inside the transaction.

## Install

```bash
dotnet add package ZeroAlloc.EventSourcing.Aggregates
dotnet add package ZeroAlloc.EventSourcing.Generators
dotnet add package ZeroAlloc.EventSourcing.InMemory
```

The generator package emits the aggregate dispatch. The AOT-safe event serialiser comes from ZeroAlloc.Serialisation, which the core package brings in.

## Quick start

```csharp
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Serialisation;

var services = new ServiceCollection()
    .AddOrderPlacedSerializer().AddItemAddedSerializer().AddSerializerDispatcher()
    .AddSingleton<IEventTypeRegistry, OrderEventTypeRegistry>(); // emitted by the generator
services.AddEventSourcing()
    .UseInMemoryEventStore() // or UsePostgreSqlEventStore, UseSqlServerEventStore, UseSqliteEventStore
    .UseAggregateRepository<Order, OrderId>(() => new Order(), id => new StreamId($"order-{id.Value}"));
await using var provider = services.BuildServiceProvider();
var orders = provider.GetRequiredService<IAggregateRepository<Order, OrderId>>();

var id = new OrderId(Guid.NewGuid());
using (var order = new Order())
{
    order.Place("alice");
    order.AddItem(9.95m);
    await orders.SaveAsync(order, id); // appends two events, checks the expected version
}

using var loaded = (await orders.LoadAsync(id)).Value; // replays the stream
Console.WriteLine($"{loaded.State.Items} item, total {loaded.State.Total}"); // 1 item, total 9.95

[ZeroAllocSerializable(SerializationFormat.SystemTextJson)] public sealed record OrderPlaced(string Customer);
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)] public sealed record ItemAdded(decimal Price);
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(ItemAdded))]
internal sealed partial class OrderJson : JsonSerializerContext;

public readonly record struct OrderId(Guid Value);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public int Items { get; private set; }
    public decimal Total { get; private set; }

    internal OrderState Apply(OrderPlaced e) => this;
    internal OrderState Apply(ItemAdded e) => this with { Items = Items + 1, Total = Total + e.Price };
}

// ApplyEvent dispatch and OrderEventTypeRegistry are source-generated: no reflection.
public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string customer) => Raise(new OrderPlaced(customer));
    public void AddItem(decimal price) => Raise(new ItemAdded(price));
}
```

The [feature tour](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/getting-started/feature-tour.md) shows consumers, projections, snapshots, Kafka and telemetry with one example each.

## What you get

- Append-only event store with optimistic concurrency through a typed `StreamPosition`, and a global stream across all aggregates.
- Aggregates with struct state, generated event dispatch and a repository that loads and saves them.
- Projections with filtering, batching and replayable rebuilds.
- Snapshots with configurable policies, so loads replay only the newer events.
- Stream consumers with checkpoints, batching, retry with backoff and FailFast, Skip or DeadLetter error handling.
- Event upcasting, dead-letter stores and health checks.
- Every package targets `net8.0`, `net9.0` and `net10.0`.

Optional packages:

- [`ZeroAlloc.EventSourcing.InMemory`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/getting-started/installation.md): in-memory store, snapshot, projection and dead-letter stores for tests.
- [`ZeroAlloc.EventSourcing.SqlServer`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/usage-guides/sql-adapters.md): SQL Server event store adapter.
- [`ZeroAlloc.EventSourcing.PostgreSql`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/usage-guides/sql-adapters.md): PostgreSQL event store adapter.
- [`ZeroAlloc.EventSourcing.Sql`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/usage-guides/sql-adapters.md): checkpoint, snapshot, projection and dead-letter stores for SQL Server and PostgreSQL.
- [`ZeroAlloc.EventSourcing.Sqlite`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/src/ZeroAlloc.EventSourcing.Sqlite/README.md): SQLite adapter for embedded and single-node use.
- [`ZeroAlloc.EventSourcing.Kafka`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/core-concepts/kafka-consumer.md): consume events from Kafka topics with the same checkpointing and error handling.
- [`ZeroAlloc.EventSourcing.Outbox`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/src/ZeroAlloc.EventSourcing.Outbox/README.md): at-least-once cross-aggregate dispatch through ZeroAlloc.Mediator.
- [`ZeroAlloc.EventSourcing.Mediator`](https://www.nuget.org/packages/ZeroAlloc.EventSourcing.Mediator): republish live committed events as ZeroAlloc.Mediator notifications.
- [`ZeroAlloc.EventSourcing.Telemetry`](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/telemetry.md): OpenTelemetry spans and metrics for aggregate loads and saves, with no OTel SDK dependency.

## Performance

Against a hand-rolled SQLite event store on the same connection, both transactional, both checking the stream version inside the transaction. .NET 8.0.26, i9-12900HK, BenchmarkDotNet v0.15.8.

| Operation | Hand-rolled | ZA.EventSourcing | Overhead |
|---|---:|---:|---:|
| Append 1 event (transactional, OCC check) | 80.7 µs / 3.80 KB | **106.3 µs / 4.79 KB** | +33% time, +26% alloc |
| Read 100-event stream (ordered) | 66.0 µs / 11.95 KB | **140.9 µs / 25.23 KB** | +114% time, +111% alloc |

The overhead is the price of typed events, pluggable serialisation, optimistic concurrency and the aggregate, projection and snapshot layers; at real-database latency the SQL round trip dominates. Full methodology and the per-call allocation budgets: [docs/performance.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/performance.md).

## Learn more

- Documentation: [eventsourcing.zeroalloc.net](https://eventsourcing.zeroalloc.net)
- Generator diagnostics: [docs/diagnostics.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/docs/diagnostics.md)
- Related: [ZeroAlloc.Mediator](https://github.com/ZeroAlloc-Net/ZeroAlloc.Mediator), [ZeroAlloc.Outbox](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox), [ZeroAlloc.Saga](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga), [ZeroAlloc.Serialisation](https://github.com/ZeroAlloc-Net/ZeroAlloc.Serialisation)
- Contributing, build and benchmark commands: [CONTRIBUTING.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/CONTRIBUTING.md)
- License: MIT, see [LICENSE](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE)
