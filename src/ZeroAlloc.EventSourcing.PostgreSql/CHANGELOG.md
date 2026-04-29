# Changelog

## 1.0.0 (2026-04-29)


### Features

* add UsePostgreSqlEventStore ([24fb1c1](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/24fb1c126e3794860f833e3a8b43943ade50fdfc))
* **postgres:** implement PostgreSqlEventStoreAdapter with optimistic concurrency ([405fed6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/405fed61117e6f895940630fcbaee630d31e3dd6))
* **postgres:** implement PostgreSqlEventStoreAdapter with optimistic concurrency ([2b69c13](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/2b69c1322bf32cfe892a82fb9edc01b603f7f43e))
* **postgres:** implement SubscribeAsync via PollingEventSubscription ([49f1aaa](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/49f1aaa6d3f477227cff75b5776d9bd3391f5339))


### Bug Fixes

* **phase3:** add missing SecondAppend test to PostgreSQL suite, normalize SqlClient param prefix, add perf TODOs in EnsureSchemaAsync ([e458f96](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/e458f96f2a736d0f249218e3941024af1ca80dc0))
* **phase3:** normalize SqlClient [@prefix](https://github.com/prefix), use hashtextextended (64-bit) for advisory locks, add metadata roundtrip and SecondAppend tests ([51e3e4d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/51e3e4d9d73a34743f091f837bb4b978e1047b9a))
* resolve Meziantou analyzer warnings (MA0002, MA0006, MA0011, MA0015, MA0025, MA0051) ([d599014](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d59901437a666fcde8c19d79d4c8103e424fd129))
* **subscriptions:** harden PollingEventSubscription and SQL adapter SubscribeAsync ([ca821b0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ca821b0cb13c1a26acde13ec36bf438b239da059))


### Code Refactoring

* add code quality analyzers and fix warnings ([47ef3b4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/47ef3b42b734041b13b6f72a12b3920c21f40b56))
* add Meziantou and Roslynator analyzers, fix core library issues ([af68333](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/af683338ad5c0d988ecdced3805485f14011b192))
* complete Meziantou analyzer fixes - reduce errors from 216 to 75 ([b1f0732](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b1f0732cff2aa4cf19216133e75c1b84d5663dc7))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
