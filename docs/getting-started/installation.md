# Installation

Get ZeroAlloc.EventSourcing up and running in minutes.

## NuGet Package

Install the core package:

```bash
dotnet add package ZeroAlloc.EventSourcing
```

### Optional Packages

**For in-memory event store:**
```bash
dotnet add package ZeroAlloc.EventSourcing.InMemory
```

**For SQL Server or PostgreSQL:**
```bash
dotnet add package ZeroAlloc.EventSourcing.Sql
```

**For aggregate source generators:**
```bash
dotnet add package ZeroAlloc.EventSourcing.Generators
```

**For source-generated, allocation-friendly serialization:**
```bash
dotnet add package ZeroAlloc.Serialisation
```

Then add a serializer adapter for your chosen format, e.g.:
```bash
dotnet add package ZeroAlloc.Serialisation.SystemTextJson
```

## Wiring Up Dependency Injection

After installing the packages, register services in your DI container. Mark your event types with
`[ZeroAllocSerializable]` so the source generator can emit the dispatcher, then call:

```csharp
services
    .AddSerializerDispatcher()  // emitted by ZeroAlloc.Serialisation source generator
    .AddEventSourcing();        // registers IEventSerializer → ZeroAllocEventSerializer
```

`AddSerializerDispatcher()` is generated per assembly at compile time — no reflection, AOT-safe.
`AddEventSourcing()` wires `IEventSerializer` to the built-in `ZeroAllocEventSerializer`, which
delegates to the dispatcher.

## Minimum Requirements

- **.NET 8+**
- **C# 12+**

## First Steps

1. Define your aggregate (see [Your First Aggregate](./first-aggregate.md))
2. Create an event store (in-memory or SQL)
3. Build your domain logic
4. Test with unit tests

## Project Structure

Recommended layout for event-sourced projects:

```
MyProject/
├── Domain/
│   ├── Aggregates/
│   │   └── Order.cs
│   └── Events/
│       ├── OrderCreated.cs
│       ├── ItemAdded.cs
│       └── OrderShipped.cs
├── Infrastructure/
│   ├── EventStore/
│   │   └── PostgreSqlEventStore.cs
│   └── Projections/
│       └── OrderProjection.cs
└── Tests/
    ├── Domain/
    │   └── OrderAggregateTests.cs
    └── Infrastructure/
        └── EventStoreIntegrationTests.cs
```

## Next Steps

- [Your First Aggregate](./first-aggregate.md) - Build your first aggregate in 5 minutes
- [Quick Start Example](./quick-start.md) - End-to-end working example
- [Core Concepts](../core-concepts/fundamentals.md) - Deep dive into event sourcing
