# ZeroAlloc.EventSourcing — Ecosystem Backlog

**Date:** 2026-04-16
**Format:** Phased roadmap — next up → near-term → long-term → advanced

This document is the canonical backlog for the ZeroAlloc.EventSourcing ecosystem. Items are organized into four phases based on dependency order and value. Phase 1 completes half-finished work; Phase 2 makes the library production-ready; Phase 3 improves developer experience; Phase 4 covers advanced scenarios.

---

## Phase 1 — Complete What's Half-Done

Foundation fixes. These are stubs, missing implementations, or constraints that block real usage. Should be addressed before new features are added.

### ✅ P1.1 — Implement `DeadLetterStrategy`

`ErrorHandlingStrategy.DeadLetter` exists in the enum and is referenced in code comments as a placeholder. The actual routing of poison messages to a dead-letter store is not wired up.

**Scope:**
- Define `IDeadLetterStore` abstraction (write-only append of failed envelopes + exception)
- `InMemoryDeadLetterStore` implementation for testing
- `PostgreSqlDeadLetterStore` and `SqlServerDeadLetterStore` SQL implementations
- Wire into `StreamConsumer` error handling path

### ✅ P1.2 — SQL-backed `IProjectionStore`

`IProjectionStore` has only an in-memory implementation. Production projections need durable state.

**Scope:**
- `PostgreSqlProjectionStore` — atomic upsert of serialized read model state
- `SqlServerProjectionStore` — parallel SQL Server implementation
- Schema: `projection_state(projection_id PK, state BYTEA/VARBINARY, updated_at)`
- Match existing snapshot store pattern for consistency

### ✅ P1.3 — `SqlServerCheckpointStore`

`PostgreSqlCheckpointStore` exists in `ZeroAlloc.EventSourcing.Sql`. SQL Server has no equivalent — consumers running on SQL Server cannot persist their position across restarts.

**Scope:**
- `SqlServerCheckpointStore : ICheckpointStore`
- Atomic MERGE upsert (matching SQL Server snapshot store pattern)
- Schema: `consumer_checkpoints(consumer_id PK, position BIGINT, updated_at DATETIME2)`
- Contract tests via existing `CheckpointStoreContractTests` base class

### P1.4 — Multi-partition Kafka support

`KafkaConsumerOptions.Partition` is a single `int` (default `0`). Real Kafka topics are partitioned; single-partition support limits throughput and prevents production use on most topics.

**Scope:**
- Change `Partition` to `int[]` or support `TopicPartition[]`
- Checkpoint tracking per partition (`consumer_id + partition` composite key)
- Assign/revoke handling for consumer group rebalancing
- Update `KafkaMessageMapper` to include partition in `StreamPosition` derivation

### ✅ P1.5 — Snapshot frequency / auto-snapshot policy

`SnapshotCachingRepositoryDecorator` exists but there is no policy for *when* to write snapshots. Callers must manage this manually, which leads to either too-frequent or no snapshot writes.

**Scope:**
- `ISnapshotPolicy` abstraction — `ShouldSnapshot(StreamPosition current, StreamPosition? lastSnapshot) → bool`
- Built-in policies: `EveryNEvents(n)`, `Always`, `Never`, `TimeBased(interval)`
- Plug into `SnapshotCachingRepositoryDecorator` as constructor parameter

---

## Phase 2 — Production Hardening

Things needed before you would trust this library in a real production system.

### ✅ P2.1 — Microsoft.Extensions.DependencyInjection integration

No `AddEventSourcing()` extension methods exist. Users wire up all abstractions manually, which is error-prone and verbose in ASP.NET Core apps.

**Scope:**
- New package: `ZeroAlloc.EventSourcing.Extensions.DependencyInjection`
- Fluent builder: `services.AddEventSourcing().UsePostgreSql(cs).UseJsonSerializer().UseInMemoryCheckpoints()`
- Adapter-specific extension packages: `.UsePostgreSql()`, `.UseSqlServer()`, `.UseKafka()`
- Lifetime management: event store as singleton, connections as transient/scoped

### ✅ P2.2 — OpenTelemetry / distributed tracing

No `Activity` spans exist anywhere. `EventMetadata` already collects `CorrelationId` and `CausationId` but they are never propagated to OpenTelemetry.

**Scope:**
- `ActivitySource` per subsystem (event store, consumer, aggregate repository)
- Span per `AppendAsync`, `ReadAsync`, consumer batch, aggregate load/save
- Propagate `CorrelationId`/`CausationId` as span attributes
- Kafka header extraction for distributed trace context propagation
- New package: `ZeroAlloc.EventSourcing.OpenTelemetry`

### ✅ P2.3 — Metrics

No metrics instrumentation. Key operational signals are invisible: append throughput, consumer lag, retry rate, dead-letter rate, snapshot hit/miss.

**Scope:**
- `System.Diagnostics.Metrics` (OTEL-compatible, no third-party dependency)
- Meters: `event_store.appends_total`, `consumer.lag`, `consumer.retries_total`, `snapshot.hits_total`, `snapshot.misses_total`
- Per-adapter tags (database type, stream id pattern)
- Expose via existing OpenTelemetry package or standalone

### ✅ P2.4 — Health checks

No `IHealthCheck` implementations. A production service cannot verify event store connectivity, consumer lag threshold, or checkpoint store reachability.

**Scope:**
- `EventStoreHealthCheck` — pings the underlying adapter
- `ConsumerLagHealthCheck` — reports unhealthy above a configurable lag threshold
- `CheckpointStoreHealthCheck` — verifies read/write round-trip
- Standard `Microsoft.Extensions.Diagnostics.HealthChecks` implementations per adapter

### ✅ P2.5 — Event schema evolution / upcasting

No strategy for when an event payload shape changes between versions. Old events that no longer match the current CLR type will fail deserialization or be silently skipped.

**Scope:**
- `IEventUpcaster<TOld, TNew>` abstraction — transforms old payload to new
- Upcaster chain registered per event type name (handles multi-version hops)
- Applied transparently during `IEventSerializer.Deserialize`
- Forward-only (no downcasting); idempotent for current-version events
- Documentation: event versioning strategy guide

### ✅ P2.6 — Built-in serializer implementations

`ZeroAllocEventSerializer` ships in the core package and wraps `ISerializerDispatcher`
(source-generated, AOT-safe). For types not annotated with `[ZeroAllocSerializable]`,
opt in to a `System.Text.Json` fallback via `services.WithSystemTextJsonFallback()` in
`ZeroAlloc.Serialisation`. Default behaviour remains strict (`NotSupportedException`) so
missing annotations are caught loudly.

---

## Phase 3 — Developer Experience

Makes the library pleasant to build on and maintain.

### P3.1 — Retry policy variants

Only `ExponentialBackoffRetryPolicy` ships today. Three common production variants are missing.

**Scope:**
- `JitteredExponentialBackoffRetryPolicy` — prevents thundering herd on correlated failures
- `CircuitBreakerRetryPolicy` — stops hammering a dead store after N consecutive failures; auto-recovers
- `FixedIntervalRetryPolicy` — simple fixed-delay for predictable retry cadence
- All implement `IRetryPolicy`; self-contained, no external dependencies

### P3.2 — `IEventStore` decorator pipeline

No built-in way to compose cross-cutting concerns (logging, metrics, caching) on `IEventStore` without subclassing. Users end up copy-pasting wrapper code.

**Scope:**
- `EventStoreBuilder.Wrap(inner).WithLogging(logger).WithMetrics().WithTracing()` fluent composition
- Each decorator is a thin `IEventStore` implementation that delegates to the next
- Analogous to ASP.NET Core middleware pipeline

### P3.3 — Aggregate `Apply` method validation via analyzer

`AggregateDispatchGenerator` generates event dispatch but does not warn when `Raise<TEvent>()` is called for an event type that has no `Apply(TEvent)` on the state struct. The event is stored but never applied to state — a silent correctness bug.

**Scope:**
- Roslyn analyzer in `ZeroAlloc.EventSourcing.Analyzers` (already a package)
- Diagnostic: `ZA0001 — No Apply method found for event type '{TEvent}' on state struct '{TState}'`
- Severity: Warning (not error, to avoid breaking builds on partial implementations)

### P3.4 — `EventStoreClient` — high-level façade

`IEventStore` requires users to wire up serializer + registry + adapter separately before getting a usable client. High barrier to entry for simple use cases.

**Scope:**
- `EventStoreClient` with fluent config builder
- Typed `AppendAsync<TEvent>(streamId, events)` / `ReadAsync<TEvent>(streamId)` signatures
- Built-in retry and error handling (delegates to `IRetryPolicy`)
- Does not replace `IEventStore` — wraps it for ergonomics

### P3.5 — Projection rebuild tooling

No built-in support for rebuilding a projection from position zero. Users must manually reset the checkpoint and re-run the consumer, with no progress visibility.

**Scope:**
- `ProjectionRebuilder` utility class
- Configurable batch size and starting position
- Progress reporting via `IProgress<RebuildProgress>` (events processed, total, elapsed)
- Optional parallel execution for independent projections
- Integrates with existing `ICheckpointStore` for checkpoint reset

### P3.6 — Testability helpers package

Tests today rely directly on `InMemoryEventStoreAdapter`. A shared testing package would eliminate boilerplate across consumer/projection unit tests.

**Scope:**
- New package: `ZeroAlloc.EventSourcing.Testing`
- Pre-built fakes: `FakeEventStore`, `FakeCheckpointStore`, `FakeSnapshotStore`
- FluentAssertions extensions: `stream.Should().ContainEvent<OrderPlaced>()`, `.HavePosition(5)`, `.BeEmpty()`
- `TestEventBus` for publishing and asserting events in isolation

---

## Phase 4 — Long-term / Advanced

High complexity. Implement once the foundation is solid and real-world usage patterns are understood.

### P4.1 — Process managers / Sagas

No orchestration layer exists. Long-running business processes spanning multiple aggregates have no home in the current design.

**Scope:**
- `ProcessManager<TState>` base class — struct state, event-driven transitions
- Durable state persistence (checkpoint + projection store backed)
- Correlation-based event routing to the correct process instance
- Timeout/deadline support
- New package: `ZeroAlloc.EventSourcing.ProcessManagers`

### P4.2 — Event migration tooling

Building on P2.5 — once upcasters exist, a full migration story is needed for rewriting historical events with the new schema.

**Scope:**
- CLI tool or hosted service for batch event rewriting
- Dry-run mode (preview changes without writing)
- Backup/snapshot before migration
- Idempotency (safe to re-run if interrupted)
- Progress and audit logging

### P4.3 — Multi-tenancy / stream namespacing

No tenant isolation today. All streams share a single table; multi-tenant apps need enforced boundaries.

**Scope:**
- Tenant context carrier (`TenantId` value type)
- Storage options: schema-per-tenant, `tenant_id` column with row-level security, or table-per-tenant
- `StreamId` extended to carry tenant context
- Adapter-level enforcement (no cross-tenant reads possible)

### P4.4 — Sharding / horizontal partitioning

Single-node per stream today. High-throughput scenarios need streams distributed across nodes.

**Scope:**
- Shard routing layer on top of `IEventStoreAdapter`
- Consistent hashing for deterministic stream-to-shard assignment
- Cross-shard global reads (`$all` stream)
- Rebalancing strategy for shard topology changes

### P4.5 — Event compression

Large payloads stored raw. No compression layer exists. A transparent decorator on `IEventSerializer` with Brotli/LZ4 per-event or per-stream would reduce storage and I/O costs.

**Scope:**
- `CompressingEventSerializer` decorator — wraps `IEventSerializer`
- Magic-byte detection for backward-compatible reads of uncompressed history
- Configurable: per-serializer threshold (only compress above N bytes)
- Benchmark comparison of storage/CPU trade-offs

### P4.6 — Encryption at rest

Sensitive domains (PII, financial) need payload encryption independent of database-level encryption.

**Scope:**
- `EncryptingEventSerializer` decorator — envelope encryption pattern
- Pluggable key management (`IKeyProvider` — local, AWS KMS, Azure Key Vault)
- Key rotation support with re-encryption tooling
- Clear migration path for unencrypted historical events

### P4.7 — Global `$all` stream optimizations

Reading the `$all` stream requires a full table scan ordered by position. At scale this becomes expensive.

**Scope:**
- Dedicated global sequence column (autoincrement/sequence per adapter)
- Covering index on global sequence
- Investigate materialized view or separate ledger table for global subscriptions
- Benchmark at 10M, 100M, 1B event counts

---

## Summary

| Phase | Theme | Items | Done | Remaining | Complexity |
|---|---|---|---|---|---|
| 1 | Complete half-done | 5 | 4 | P1.4 | Low–Medium |
| 2 | Production hardening | 6 | 6 | — | Medium–High |
| 3 | Developer experience | 6 | 0 | all | Low–Medium |
| 4 | Long-term / advanced | 7 | 0 | all | High |
