# Changelog

## 1.0.0 (2026-04-29)


### Features

* add ISnapshotStoreProvider factory for runtime database selection ([7f1510f](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/7f1510f7b30b09cc7c81c38064e1c1ab40c81961))
* add PostgreSqlEventStoreHealthCheck and PostgreSqlCheckpointStoreHealthCheck ([034856d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/034856d080c8b746fb6a1c58528443e096566442))
* add shared snapshot table schema for PostgreSQL and SQL Server ([1467b3c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/1467b3c106870a3d28cdfb75e9af7c0f03e948b7))
* add UsePostgreSql*/UseSqlServer* checkpoint/snapshot/dead-letter/projection stores ([b1282bd](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b1282bdce9d9b99d343d9e3b96a043d7b9b4ef93))
* implement Phase 1 stream consumers infrastructure ([602eee2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/602eee2feb83c385736dbf9af366d1dd56a3ca8e))
* implement PostgreSqlCheckpointStore with atomic upsert ([4603bb6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/4603bb6e0ec47b24bbd69b7a67929df7a3e223b7))
* implement PostgreSqlSnapshotStore with BYTEA payload and ON CONFLICT upsert ([b1fc532](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b1fc532573d2455ab1ce52823e95b4f55cfbe1e3))
* implement SqlServerSnapshotStore with VARBINARY payload and MERGE upsert ([c512e7a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c512e7a4e6c7fdff109e477bf0d9c341e7580c17))
* Phase 1 completion - SqlServerCheckpointStore, ISnapshotPolicy, IDeadLetterStore, IProjectionStore, multi-partition Kafka ([#43](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/43)) ([b0b13ef](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b0b13ef91a98b686832d1277f8a12215ed1fff7d))


### Bug Fixes

* add StringComparison.Ordinal to string.Equals calls and restore nameof() in Validate ([65a35a8](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/65a35a878c5aefc90b1df46fa2ae30a0336f4984))
* each PostgreSQL health check registration owns its NpgsqlDataSource independently ([b83cb5f](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b83cb5fa8839b51f90388ae4b3b0b5cce51b4653))
* resolve all code quality analyzer warnings ([7cd63c7](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/7cd63c792bf9152ddceff1512b651a52a6e0f844))
* use idiomatic await using var for NpgsqlCommand disposal in PostgreSQL health checks ([5a9bae5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5a9bae5c5c8c0480305dcbb952f3572ab0776fe3))


### Code Refactoring

* add code quality analyzers and fix warnings ([47ef3b4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/47ef3b42b734041b13b6f72a12b3920c21f40b56))
* add ConfigureAwait(false) and fix async patterns for library code ([0d8f842](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0d8f842328fa99bd8ce40d1efb784fbd63742ea9))
* add Meziantou and Roslynator analyzers, fix core library issues ([af68333](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/af683338ad5c0d988ecdced3805485f14011b192))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
