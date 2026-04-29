# Changelog

## 1.0.0 (2026-04-29)


### ⚠ BREAKING CHANGES

* **eventsourcing.telemetry:** WithTelemetry() no longer instruments IEventStore. Anyone subscribed to event_store.* span names needs to switch to aggregate.* names.

### Features

* add InstrumentedAggregateRepository decorator ([9cd893a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/9cd893a544e533fafc7a40eebef3ea07d3d093ab))
* add InstrumentedEventStore decorator with Activity and Counter instrumentation ([2f6fa84](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/2f6fa84ddb5e904ffaac1c95570ff0fee547d38e))
* add InstrumentedStreamConsumer and UseEventSourcingTelemetry DI extension ([0d10484](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0d1048404b386ee29e81dff5a4de1c717fd67a25))
* add ZeroAlloc.EventSourcing.Telemetry package scaffold ([e1300bb](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/e1300bbac17a1ba3a36ebed37214e7a008f2a74e))
* **eventsourcing.telemetry:** add WithTelemetry alias for UseEventSourcingTelemetry ([#78](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/78)) ([dfbc93b](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/dfbc93b3f1b335ebce9b23b7824169ac185bef40))
* **eventsourcing.telemetry:** WithTelemetry() now decorates IAggregateRepository&lt;,&gt; ([#68](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/68)) ([#79](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/79)) ([48d3faf](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/48d3faff322c1b1b9443fdef5b103eba238e72b2))
* propagate correlation.id and causation.id baggage as span tags ([7dae77d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/7dae77d1a62e066e37ce8f6e2d4495f03f9b83a5))
* telemetry spans via [Instrument] + cross-repo NuGet isolation ([#71](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/71)) ([527737e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/527737e117ba411a612dbb012c0e325bbc5aeeac))


### Bug Fixes

* only increment counters on Result success; document static ActivitySource/Meter intent ([baa20ce](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/baa20ce26ff61d383a69ead559a6b870d8466322))
* throw when UseEventSourcingTelemetry called without IEventStore; dispose ServiceProvider in tests ([eefa99a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/eefa99a6de77481e4d351b16f9fdcd97c1507bed))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
