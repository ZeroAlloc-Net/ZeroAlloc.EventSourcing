# Changelog

## [1.1.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.0.0...ZeroAlloc.EventSourcing-v1.1.0) (2026-05-01)


### Features

* lock public API surface (PublicApiAnalyzers + api-compat gate) ([#113](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/113)) ([9be0a0e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/9be0a0e3bdb6dd761a32dc7db7f677680a5480f4))

## 1.0.0 (2026-04-29)


### ⚠ BREAKING CHANGES

* **eventsourcing.telemetry:** WithTelemetry() no longer instruments IEventStore. Anyone subscribed to event_store.* span names needs to switch to aggregate.* names.

### Features

* add AddEventSourcing() — registers ZeroAllocEventSerializer as default IEventSerializer ([74a4357](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/74a43573142217e7ff142d2b73546154cb7dbf76))
* add EventSourcingBuilder + UseInMemoryCheckpointStore ([d6513ad](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d6513adcca509951d9196c7055d3c3ca8fd2316e))
* add ICheckpointStore interface for consumer position management ([ab043f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ab043f694c336d6098c8d10173baf784da89fba7))
* add ICheckpointStore interface for consumer position management ([ef8dbaa](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ef8dbaa676e8e81ab8035c6a998bd37cbc64ce1a))
* add IEventUpcaster, IUpcasterPipeline, UpcasterRegistration abstractions ([1f3f74d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/1f3f74dbf2219088640d86dcfc93092c9dbfce73))
* add IProjectionStore and InMemoryProjectionStore ([786e94c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/786e94c7227e35af3a6ca0578a3c8a7a09453813))
* add ISnapshotStore&lt;TState&gt; interface and contract tests ([98e54f9](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/98e54f99e7933331c6f74ba7bce12d95d9e7d478))
* add Projection&lt;TReadModel&gt; base class for read models ([9fd3694](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/9fd369447449af98fa7b0eb5094d66f8de098c60))
* add SnapshotLoadingStrategy enum for configurable snapshot behavior ([c132c2b](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c132c2bf9e9e6630f6a04deb2db8ca49a50052bb))
* add SnapshotLoadingStrategy enum for configurable snapshot behavior ([8cbde8e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8cbde8e65ed30e3432bf1dfc0720e4a16e4e8abb))
* add StreamConsumerOptions with configuration validation ([6e19907](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/6e19907d84024868a7826ac3e82a450d74bb0ecd))
* add UpcasterPipeline — auto-chaining IUpcasterPipeline implementation ([ab8468f](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ab8468f8b37003945fc67e495d5c46ad21797d1b))
* add ZeroAllocEventSerializer backed by ISerializerDispatcher ([50f8fd4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/50f8fd40c26bc1035e7f8ec5a08ff2ea120a8259))
* **aggregates:** implement AggregateRepository with load/save and optimistic concurrency ([dd03d96](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/dd03d96de3192555d32968864bdc8ce0cc060add))
* **core:** add IEventStore, IEventStoreAdapter, IEventTypeRegistry, IEventSubscription, IEventSerializer ([097d51d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/097d51d357ca7f33f365db34571723a3dd514587))
* **core:** add StreamId, StreamPosition, EventEnvelope, EventMetadata, RawEvent, AppendResult, StoreError ([f2500bd](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/f2500bd682804b8455d95c2666a1513acaaa2cd4))
* **core:** implement EventStore facade with serialization and type registry ([375b488](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/375b48835124dc31106d55cf6a98f3d30e7b65b6))
* **eventsourcing.telemetry:** WithTelemetry() now decorates IAggregateRepository&lt;,&gt; ([#68](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/68)) ([#79](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/79)) ([48d3faf](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/48d3faff322c1b1b9443fdef5b103eba238e72b2))
* implement BatchedProjection ([f89009e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/f89009e1b58abfe10ea90fc9de75ff20baf41d65))
* implement FilteredProjection for selective event processing ([2b76032](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/2b7603297da798a762e96ddf1ebbd0978412402b))
* implement InMemoryCheckpointStore for testing ([36cd069](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/36cd069422f3b1e534cf3ddaf467b2570ccf953b))
* implement InMemoryCheckpointStore for testing ([c188cdc](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c188cdcda338cf044e27fd2c1bf217ad06fca2da))
* implement Phase 1 stream consumers infrastructure ([602eee2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/602eee2feb83c385736dbf9af366d1dd56a3ca8e))
* implement ProjectionConsistencyChecker ([6461466](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/64614661d43abe015cc16c797211ae5a877f09d2))
* implement ReplayableProjection with rebuilding ([5da5578](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5da5578f1f0857fb09593ec916a0528554d2701d))
* implement StreamConsumer with batch processing and retry logic ([722cd13](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/722cd138b4bf0bd47c92dd62349dc9fceff7f4a4))
* Phase 1 completion - SqlServerCheckpointStore, ISnapshotPolicy, IDeadLetterStore, IProjectionStore, multi-partition Kafka ([#43](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/43)) ([b0b13ef](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b0b13ef91a98b686832d1277f8a12215ed1fff7d))
* Phase 6 - Kafka Integration ([05d2055](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/05d20556ff30587bce52808b5b308831136202fc))
* **postgres:** implement SubscribeAsync via PollingEventSubscription ([49f1aaa](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/49f1aaa6d3f477227cff75b5776d9bd3391f5339))
* **subscriptions:** add PollingEventSubscription for catch-up + poll-based delivery ([33658a9](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/33658a9195cb53f3ac659671a7e98d146f1dd3a4))
* telemetry spans via [Instrument] + cross-repo NuGet isolation ([#71](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/71)) ([527737e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/527737e117ba411a612dbb012c0e325bbc5aeeac))
* wire IUpcasterPipeline into EventStore and register via AddEventSourcing ([d85c487](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d85c487d0c050856441d3cdf01c3d093ef3f2f22))


### Bug Fixes

* address code review — CI matrix, design doc, thread-safety comments, StreamId guard, facade subscription test ([d0367a6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d0367a609b0ea4542dc3ba01cd3653506c5bba95))
* address final review issues (position comment, Subscribe void, internal OrderPlaced, TCS in tests) ([5c05d6c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5c05d6c08a5c7b9df1aefd43ce2d0f46d933516b))
* correct stale cref to EventSourcingBuilderUpcasterExtensions, remove CS1574 pragmas ([09aa1a5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/09aa1a59022b0e3cba59a6326110390f6ddad6ee))
* correct target framework and add benchmark attributes for spec compliance ([2f676a5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/2f676a5e2aa65b501885309d24184ca35e3707c5))
* implement continuous batch processing and fix manual commit strategy ([69b13ee](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/69b13ee20744d147bb246e8db80544f6fffce846))
* move ZeroAlloc.Telemetry and ZeroAlloc.StateMachine into CPM; bump Meziantou.Analyzer ([0cc4c3e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0cc4c3ed7d5bf2852ddc4ed9acfe595d5ba6d0b3))
* move ZeroAlloc.Telemetry and ZeroAlloc.StateMachine into CPM; bump Meziantou.Analyzer ([4918308](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/4918308f11eb8041bfa803e16e773a55feb82f13))
* resolve rebase conflicts and implement CommitAsync in StreamConsumer ([1d0c95c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/1d0c95c5fede6bb31270f83f490fc01b639b1872))
* **subscriptions:** atomic dispose/start guards, null guards in ctor, use Position.Next() ([5a128f9](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5a128f9d1cc1d6adf5950bd1730f90054af84e1e))
* **subscriptions:** harden PollingEventSubscription and SQL adapter SubscribeAsync ([ca821b0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ca821b0cb13c1a26acde13ec36bf438b239da059))
* tighten CS1574 pragma scope, add NotNullWhen annotation, minor doc fixes ([8ef6d32](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8ef6d32929d678a7b705b4c8ee5595f4459042bd))
* UpcasterPipeline — out object nullability, duplicate key error, test improvements ([18e28df](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18e28dfab59c55ca5b867197806dec02b18ccc3e))


### Code Refactoring

* add code quality analyzers and fix warnings ([47ef3b4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/47ef3b42b734041b13b6f72a12b3920c21f40b56))
* add CommitAsync method and complete error strategy tests ([74dfde3](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/74dfde3a7cde56d9d40238581b77e0f82871e4c3))
* add Meziantou and Roslynator analyzers, fix core library issues ([af68333](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/af683338ad5c0d988ecdced3805485f14011b192))
* **core:** add ConfigureAwait(false) to ReadAsync and constructor null-guards in EventStore ([a8db95a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/a8db95a5a23a6bfb0f69e3f9da69a76e4528cdfb))
* fix exponential backoff overflow vulnerability and add edge case tests ([143c27e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/143c27ed3be08a85a5283c0134cc5bcda21462ff))
* improve checkpoint store documentation and thread-safety test ([42dd850](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/42dd8505ef8e1d04a232c2baf2c29465ddd262e7))
* minor post-review improvements (variable naming, dedup csproj ref, consolidate Kafka extensions) ([b5cd55a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b5cd55a3decb973ffdcd45b32813c912ea14ea02))
* standardize checkpoint store naming and test assertions ([a5b0444](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/a5b04449895c1cd37a0c9b49543cda30531011cf))
* standardize checkpoint store naming and test assertions ([8f9d24d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8f9d24d338d2dde50ed58ee0b5a220135bf16b0f))


### Documentation

* add remarks warning on UseInMemoryCheckpointStore ([9e06109](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/9e06109e1cac620055dadbf0351b8991dc1c29c1))
* **core:** clarify EventEnvelope boxing semantics and RawEvent equality caveat ([7af0267](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/7af0267bea85f0817739590d7ff1cae9e7f5b355))


### Tests

* add integration tests for stream consumer workflow ([bbedbb1](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/bbedbb15baddf4982f3d9e7e8688533befc24c7f))
* add SubscribeAsync upcast coverage; docs: singleton lifetime warning on class-based AddUpcaster ([018f2a5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/018f2a533ee2174e05d272aa67d310dc0ba88f0f))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
