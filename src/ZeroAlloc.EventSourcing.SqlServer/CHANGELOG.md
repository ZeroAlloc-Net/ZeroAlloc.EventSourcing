# Changelog

## 1.0.0 (2026-04-29)


### Features

* add SqlServerEventStoreHealthCheck and IHealthChecksBuilder extension ([b05264f](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b05264fa8393b1c7df6c2863746c3b7c66a6bfa9))
* add UseSqlServerEventStore ([ce49ef1](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/ce49ef1a2fcc7ecff3e24cf6adf4b93eae7378aa))
* **sqlserver:** implement SqlServerEventStoreAdapter with optimistic concurrency ([bc44683](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/bc44683a7bf57c6647b431751a201953c0131b03))
* **sqlserver:** implement SqlServerEventStoreAdapter with optimistic concurrency ([1e3074d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/1e3074db113d546443369806f3df368aed32d837))
* **sqlserver:** implement SubscribeAsync via PollingEventSubscription ([915d0ca](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/915d0ca4cda0b3924cc5ad43699547d7e8ae07b2))


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
