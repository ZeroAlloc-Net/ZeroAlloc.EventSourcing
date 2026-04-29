# Changelog

## 1.0.0 (2026-04-29)


### Features

* add UseAggregateRepository ([a5a2bff](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/a5a2bffaa78d156f43e62ddb06f10427093efaf0))
* **aggregates:** add IAggregateState&lt;TSelf&gt; and IAggregateRepository&lt;TAggregate,TId&gt; ([8ce4198](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8ce4198ab222e22d21fcfd744ad8a73969db6909))
* **aggregates:** implement Aggregate&lt;TId,TState&gt; base class with HeapPooledList ([7d4ebd4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/7d4ebd4111d582c94405c071ad637eeed7f7e41d))
* **aggregates:** implement AggregateRepository with load/save and optimistic concurrency ([dd03d96](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/dd03d96de3192555d32968864bdc8ce0cc060add))
* **generators:** implement AggregateDispatchGenerator — source-generated ApplyEvent switch ([6c0a037](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/6c0a0370fd1aa95e6e0ec8c4578ca029351e013e))
* implement SnapshotCachingRepositoryDecorator with configurable strategies ([eb52c10](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/eb52c10023a74c4a62cd629c593eebb6a77c08d6))
* Phase 1 completion - SqlServerCheckpointStore, ISnapshotPolicy, IDeadLetterStore, IProjectionStore, multi-partition Kafka ([#43](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/43)) ([b0b13ef](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b0b13ef91a98b686832d1277f8a12215ed1fff7d))
* telemetry spans via [Instrument] + cross-repo NuGet isolation ([#71](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/71)) ([527737e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/527737e117ba411a612dbb012c0e325bbc5aeeac))


### Bug Fixes

* **aggregates:** update OriginalVersion after successful save via IAggregate.AcceptVersion ([755ceae](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/755ceae40f08c225a843ddf65dfce1ddcdf6cb67))
* **generators:** restrict Apply method filter to internal only, document Dispose thread-safety ([29701af](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/29701af1bc07f317e2d30dda580e9e56177dd0c8))
* move ZeroAlloc.Telemetry and ZeroAlloc.StateMachine into CPM; bump Meziantou.Analyzer ([0cc4c3e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0cc4c3ed7d5bf2852ddc4ed9acfe595d5ba6d0b3))
* move ZeroAlloc.Telemetry and ZeroAlloc.StateMachine into CPM; bump Meziantou.Analyzer ([4918308](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/4918308f11eb8041bfa803e16e773a55feb82f13))


### Code Refactoring

* add code quality analyzers and fix warnings ([47ef3b4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/47ef3b42b734041b13b6f72a12b3920c21f40b56))
* add Meziantou and Roslynator analyzers, fix core library issues ([af68333](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/af683338ad5c0d988ecdced3805485f14011b192))
* **aggregates:** simplify dispatch direction in Aggregate.cs, document State visibility ([5906a50](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5906a509f84a7585e2f6cc3f629e9c9d5939e566))
* complete Meziantou analyzer fixes - reduce errors from 216 to 75 ([b1f0732](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b1f0732cff2aa4cf19216133e75c1b84d5663dc7))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
