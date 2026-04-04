# ZeroAlloc.EventSourcing Documentation Index

## Quick Navigation

### Getting Started
- **[Installation](getting-started/installation.md)** - Install the NuGet package
- **[Quick Start Example](getting-started/quick-start-example.md)** - 5-minute intro
- **[Your First Aggregate](getting-started/first-aggregate.md)** - Build your first aggregate

### Core Concepts (Read These First)
- **[Event Sourcing Fundamentals](core-concepts/fundamentals.md)** - Core principles and motivations
- **[Aggregates](core-concepts/aggregates.md)** - Domain entities that handle commands and raise events
- **[Events](core-concepts/events.md)** - Immutable facts that model what happened
- **[Event Store](core-concepts/event-store.md)** - How events are persisted and retrieved
- **[Snapshots](core-concepts/snapshots.md)** - Optimize large aggregates with snapshots
- **[Projections](core-concepts/projections.md)** - Build read models for queries
- **[Architecture](core-concepts/architecture.md)** - System design and patterns

### Code Examples (Learn by Doing)
- **[Getting Started Examples](examples/01-getting-started/)** - CreateFirstAggregate, AppendAndRead
- **[Domain Modeling Examples](examples/02-domain-modeling/)** - OrderAggregate, OrderState, OrderEvents
- **[Testing Examples](examples/03-testing/)** - TestingAggregates, TestingProjections
- **[Advanced Examples](examples/04-advanced/)** - CustomEventStore, CustomProjection, CustomSnapshotStore

### Performance & Optimization
- **[Performance Characteristics](performance/characteristics.md)** - Latency, throughput, allocation profiles
- **[Zero-Allocation Design](performance/zero-allocation.md)** - Struct-based state and performance philosophy
- **[Optimization Strategies](performance/optimization.md)** - Snapshots, batching, filtering, parallel loads
- **[Benchmark Results](performance/benchmarks.md)** - Detailed measurements and methodology

### Advanced Topics
- **[Custom Event Stores](advanced/custom-event-store.md)** - Implement for SQL Server, PostgreSQL, MongoDB
- **[Custom Snapshot Stores](advanced/custom-snapshots.md)** - Snapshot patterns and strategies
- **[Advanced Projections](advanced/custom-projections.md)** - 10 patterns: filtering, composition, side effects, etc.
- **[Plugin Architecture](advanced/plugin-architecture.md)** - Build extensible systems with plugins
- **[Contributing Guide](advanced/contributing.md)** - How to contribute to the project

### Additional Resources
- **[Adoption Guide](ADOPTION_GUIDE.md)** - Business case and adoption strategy
- **[Latest News](../PHASE5_FINAL_REVIEW.md)** - Recent updates and improvements

## Learning Paths

### Path 1: Learn Event Sourcing (1-2 hours)
1. [Event Sourcing Fundamentals](core-concepts/fundamentals.md)
2. [Your First Aggregate](getting-started/first-aggregate.md)
3. [Examples: CreateFirstAggregate](examples/01-getting-started/CreateFirstAggregate.cs)
4. [Examples: AppendAndRead](examples/01-getting-started/AppendAndRead.cs)

### Path 2: Build Your First System (2-3 hours)
1. [Installation](getting-started/installation.md)
2. [Aggregates](core-concepts/aggregates.md)
3. [Events](core-concepts/events.md)
4. [Examples: OrderAggregate](examples/02-domain-modeling/OrderAggregate.cs)
5. [Examples: TestingAggregates](examples/03-testing/TestingAggregates.cs)

### Path 3: Optimize for Production (1-2 hours)
1. [Performance Characteristics](performance/characteristics.md)
2. [Zero-Allocation Design](performance/zero-allocation.md)
3. [Optimization Strategies](performance/optimization.md)
4. [Snapshots](core-concepts/snapshots.md)
5. [Custom Snapshot Stores](advanced/custom-snapshots.md)

### Path 4: Extend the Framework (2-3 hours)
1. [Custom Event Stores](advanced/custom-event-store.md)
2. [Advanced Projections](advanced/custom-projections.md)
3. [Plugin Architecture](advanced/plugin-architecture.md)
4. [Examples: CustomEventStore](examples/04-advanced/CustomEventStore.cs)

### Path 5: Contribute to the Project (1-2 hours)
1. [Contributing Guide](advanced/contributing.md)
2. [Examples: TestingAggregates](examples/03-testing/TestingAggregates.cs)
3. Fork, branch, test, and submit PR

## By Topic

### Understanding Event Sourcing
- [Event Sourcing Fundamentals](core-concepts/fundamentals.md) - What and why
- [Aggregates](core-concepts/aggregates.md) - Command handlers
- [Events](core-concepts/events.md) - Immutable facts
- [Event Store](core-concepts/event-store.md) - Persistence

### Building Applications
- [Quick Start Example](getting-started/quick-start-example.md)
- [Domain Modeling Examples](examples/02-domain-modeling/)
- [Architecture](core-concepts/architecture.md)
- [Snapshots](core-concepts/snapshots.md)
- [Projections](core-concepts/projections.md)

### Testing
- [Testing Examples](examples/03-testing/) - Unit and integration tests
- [Examples: TestingAggregates](examples/03-testing/TestingAggregates.cs) - Aggregate tests
- [Examples: TestingProjections](examples/03-testing/TestingProjections.cs) - Projection tests

### Performance
- [Performance Characteristics](performance/characteristics.md) - Understand performance
- [Zero-Allocation Design](performance/zero-allocation.md) - Design for performance
- [Optimization Strategies](performance/optimization.md) - Improve performance
- [Benchmark Results](performance/benchmarks.md) - Real data

### Custom Implementations
- [Custom Event Stores](advanced/custom-event-store.md) - SQL, MongoDB, etc.
- [Custom Snapshots](advanced/custom-snapshots.md) - Redis, S3, etc.
- [Advanced Projections](advanced/custom-projections.md) - Complex read models
- [Plugin Architecture](advanced/plugin-architecture.md) - Extensible systems

## File Organization

```
docs/
├── INDEX.md (this file)
├── ADOPTION_GUIDE.md
├── getting-started/
│   ├── installation.md
│   ├── quick-start-example.md
│   └── first-aggregate.md
├── core-concepts/
│   ├── fundamentals.md
│   ├── aggregates.md
│   ├── events.md
│   ├── event-store.md
│   ├── snapshots.md
│   ├── projections.md
│   └── architecture.md
├── examples/
│   ├── 01-getting-started/
│   │   ├── CreateFirstAggregate.cs
│   │   └── AppendAndRead.cs
│   ├── 02-domain-modeling/
│   │   ├── OrderAggregate.cs
│   │   ├── OrderEvents.cs
│   │   └── OrderState.cs
│   ├── 03-testing/
│   │   ├── TestingAggregates.cs
│   │   └── TestingProjections.cs
│   └── 04-advanced/
│       ├── CustomEventStore.cs
│       ├── CustomProjection.cs
│       └── CustomSnapshotStore.cs
├── performance/
│   ├── characteristics.md
│   ├── zero-allocation.md
│   ├── optimization.md
│   └── benchmarks.md
└── advanced/
    ├── custom-event-store.md
    ├── custom-snapshots.md
    ├── custom-projections.md
    ├── plugin-architecture.md
    └── contributing.md
```

## Key Statistics

- **19 documentation/example files**
- **7,000+ lines of documentation**
- **2,673 lines of runnable C# code**
- **Covers all major concepts and patterns**
- **Real-world examples and benchmarks**

## Need Help?

- Check the [FAQ](getting-started/quick-start-example.md#faq) in Quick Start
- Read [Architecture Guide](core-concepts/architecture.md) for system design
- See [Contributing Guide](advanced/contributing.md) for development setup
- Open an issue or discussion on GitHub

## Document Status

- **Performance & Benchmarks**: Complete (4 files, 2,050 lines)
- **Advanced Topics**: Complete (5 files, 3,277 lines)
- **Code Examples**: Complete (10 files, 2,673 lines)

Last Updated: 2026-04-04
