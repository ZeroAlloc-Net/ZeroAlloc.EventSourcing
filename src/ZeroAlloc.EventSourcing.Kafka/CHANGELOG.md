# Changelog

## 1.0.0 (2026-04-29)


### Features

* add Kafka package infrastructure and projects ([a6b5d73](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/a6b5d7334307a64c3ff72cda9bb64193b9b687e9))
* add KafkaBaseOptions, KafkaManualPartitionOptions, KafkaConsumerGroupOptions (P1.4 Task 1) ([319396f](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/319396f37f3b2422ba992d173161e1294338efa9))
* add KafkaConsumerBase abstract base class ([bf9e23c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/bf9e23c316cb423e063819ae49b0fb2f76616bb0))
* add KafkaConsumerGroupConsumer with async-safe rebalancing ([cc53c2b](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/cc53c2b65626a83719ac4620cd0f22bf60eb39ae))
* add KafkaEventStoreHealthCheck and IHealthChecksBuilder extension ([536afd4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/536afd49e60bb64a31ff6f3b09a18c3f87f21ec3))
* add KafkaManualPartitionConsumer ([63363b5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/63363b5fde3ad6dfc8285b7a4f025ebe472a3782))
* add UseKafka ([8029864](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8029864ca72ac0e3c560fbda09f986015897378f))
* add UseKafkaManualPartitions + UseKafkaConsumerGroup DI extensions, remove UseKafka ([1805c82](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/1805c825c52dffef7842b49a219b46cbd488ce3d))
* implement KafkaStreamConsumer and core Kafka integration ([5c7a619](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5c7a6193479a05c7369dc13ef28ec5cee2fb8c88))
* Phase 1 completion - SqlServerCheckpointStore, ISnapshotPolicy, IDeadLetterStore, IProjectionStore, multi-partition Kafka ([#43](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/43)) ([b0b13ef](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b0b13ef91a98b686832d1277f8a12215ed1fff7d))
* Phase 6 - Kafka Integration ([05d2055](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/05d20556ff30587bce52808b5b308831136202fc))


### Bug Fixes

* add EnableAutoOffsetStore=false to KafkaConsumerGroupConsumer config ([74d418c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/74d418c7d845ba0f97c6e4ae7d1e66082cbc17b8))
* add StringComparison.Ordinal to string.Equals calls and restore nameof() in Validate ([65a35a8](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/65a35a878c5aefc90b1df46fa2ae30a0336f4984))
* address code review feedback on Kafka options classes ([3613b93](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/3613b9363c73b368b6737083747d29747c2a4bb3))
* address code review feedback on KafkaConsumerBase ([46efd06](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/46efd06e97d5c67aad1ff6f6da5f85afa52b2236))
* address code review feedback on KafkaManualPartitionConsumer ([eba8b61](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/eba8b61d818936a83f75d7d2f328787c9a666e43))
* handle ObjectDisposedException when closing Kafka consumer ([760ce41](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/760ce41a1f9d8a101d3486dd5cf853cafb67e9e4))
* remove dead OnRevoked hook, add StoreOffset for revoke correctness, delete KafkaCheckpointStore ([6c799d0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/6c799d0df5b21327ceb927f526ade90e8c750bc4))
* resolve Meziantou analyzer warnings (MA0002, MA0006, MA0011, MA0015, MA0025, MA0051) ([d599014](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d59901437a666fcde8c19d79d4c8103e424fd129))
* reuse AdminClient across Kafka health check invocations; offload blocking call to Task.Run ([2266e13](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/2266e13634a7b224ed41b654a25df32810d34eda))


### Code Refactoring

* add code quality analyzers and fix warnings ([47ef3b4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/47ef3b42b734041b13b6f72a12b3920c21f40b56))
* add Meziantou and Roslynator analyzers, fix core library issues ([af68333](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/af683338ad5c0d988ecdced3805485f14011b192))
* complete Meziantou analyzer fixes - reduce errors from 216 to 75 ([b1f0732](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b1f0732cff2aa4cf19216133e75c1b84d5663dc7))
* minor post-review improvements (variable naming, dedup csproj ref, consolidate Kafka extensions) ([b5cd55a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b5cd55a3decb973ffdcd45b32813c912ea14ea02))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
