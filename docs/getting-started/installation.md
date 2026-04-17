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

### With ZeroAlloc.Serialisation (recommended)

Mark each event type with `[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]`, create a
`JsonSerializerContext` for the type, then register in DI:

```csharp
// Annotate your event types
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public record OrderPlacedEvent(string OrderId, decimal Total);

// Provide AOT-safe type metadata
[JsonSerializable(typeof(OrderPlacedEvent))]
internal partial class DomainJsonContext : JsonSerializerContext { }

// Wire up services
services
    .AddJsonSerializer<OrderPlacedEvent>(DomainJsonContext.Default.OrderPlacedEvent)
    .AddSerializerDispatcher()  // emitted by ZeroAlloc.Serialisation source generator
    .AddEventSourcing()         // registers IEventSerializer → ZeroAllocEventSerializer; returns EventSourcingBuilder
    .UseInMemoryEventStore();   // swap for .UsePostgreSqlEventStore(cs) or .UseSqlServerEventStore(cs) in production
```

`AddSerializerDispatcher()` is generated per assembly at compile time — no reflection, AOT-safe.
`AddEventSourcing()` wires `IEventSerializer` to the built-in `ZeroAllocEventSerializer` and returns
an `EventSourcingBuilder`. Chain `.Use*()` calls on the builder to register the store adapter.

### Without ZeroAlloc.Serialisation (custom serializer)

Register your own `IEventSerializer` before calling `AddEventSourcing()`, or skip that call
and register `IEventSerializer` directly:

```csharp
services.AddSingleton<IEventSerializer, MyJsonEventSerializer>();
```

See [Custom serializer](../core-concepts/events.md#custom-implementing-ieventserializer-yourself)
for a full example.

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
