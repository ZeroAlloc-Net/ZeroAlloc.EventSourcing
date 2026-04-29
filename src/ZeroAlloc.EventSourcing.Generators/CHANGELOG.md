# Changelog

## 1.0.0 (2026-04-29)


### Features

* add ProjectionDispatchGenerator for compiled event dispatch ([80e40f9](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/80e40f99b479712a70e6905ffd41faae61022c99))
* **generators:** emit IEventTypeRegistry implementation per aggregate ([b6c5c20](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b6c5c2040ebbcaf026141117260668166cb8669e))
* **generators:** implement AggregateDispatchGenerator — source-generated ApplyEvent switch ([6c0a037](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/6c0a0370fd1aa95e6e0ec8c4578ca029351e013e))
* Phase 1 completion - SqlServerCheckpointStore, ISnapshotPolicy, IDeadLetterStore, IProjectionStore, multi-partition Kafka ([#43](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/43)) ([b0b13ef](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b0b13ef91a98b686832d1277f8a12215ed1fff7d))


### Bug Fixes

* correct Roslyn VersionOverride in Generators csproj (5.3.0 -&gt; 5.0.0) ([0d411e2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0d411e24a656abbe9e4bd05b9567eed1ce077f97))
* correct UsePostgreSqlDeadLetterStore IEventSerializer remarks ([0d411e2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0d411e24a656abbe9e4bd05b9567eed1ce077f97))
* **generators:** restrict Apply method filter to internal only, document Dispose thread-safety ([29701af](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/29701af1bc07f317e2d30dda580e9e56177dd0c8))
* **generators:** value equality on AggregateInfo, namespace-qualified hint names, expanded tests ([8d8fa67](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8d8fa6728bb955cfc8781849a106c6cd74af703d))
* remove fallback ApplyTyped from ProjectionTests now that generator runs correctly ([0d411e2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0d411e24a656abbe9e4bd05b9567eed1ce077f97))
* **subscriptions:** harden PollingEventSubscription and SQL adapter SubscribeAsync ([ca821b0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ca821b0cb13c1a26acde13ec36bf438b239da059))


### Code Refactoring

* **generators:** rename shared helpers to IsPartialClassWithBaseSyntax/GetAggregateInfoPublic, add docs and missing test ([c14107e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c14107e64007409eb5536390fe0e78eea05a1e4f))


### Tests

* add DoesNotOverwrite/ReturnsBuilder tests for all Sql builder extensions ([0d411e2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0d411e24a656abbe9e4bd05b9567eed1ce077f97))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
