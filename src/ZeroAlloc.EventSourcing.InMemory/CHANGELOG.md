# Changelog

## 1.0.0 (2026-04-29)


### Features

* add InMemoryEventStoreHealthCheck and IHealthChecksBuilder extension ([b986de1](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b986de11588aaaaaba16dfa96db385201c1553f7))
* add InMemorySnapshotStore implementation ([6002378](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/6002378ee296d269120b869eae4b83cd4b5c849d))
* add IProjectionStore and InMemoryProjectionStore ([786e94c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/786e94c7227e35af3a6ca0578a3c8a7a09453813))
* add UseInMemoryEventStore/SnapshotStore/DeadLetterStore/ProjectionStore ([88a20b9](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/88a20b9cafa2c1f2c318be652c5fb6da50dd7aaf))
* implement Phase 1 stream consumers infrastructure ([602eee2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/602eee2feb83c385736dbf9af366d1dd56a3ca8e))
* implement StreamConsumer with batch processing and retry logic ([722cd13](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/722cd138b4bf0bd47c92dd62349dc9fceff7f4a4))
* **inmemory:** add live subscriptions via ZeroAlloc.AsyncEvents ([430d78a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/430d78a61c79ad6088d421963fef7e2ef00f25f7))
* **inmemory:** implement InMemoryEventStoreAdapter append and read ([eaabeac](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/eaabeac1e4bfa77a9602e0139eb28bcfdd1794c2))
* Phase 1 completion - SqlServerCheckpointStore, ISnapshotPolicy, IDeadLetterStore, IProjectionStore, multi-partition Kafka ([#43](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/43)) ([b0b13ef](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b0b13ef91a98b686832d1277f8a12215ed1fff7d))


### Bug Fixes

* add StringComparison.Ordinal to string.Equals calls and restore nameof() in Validate ([65a35a8](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/65a35a878c5aefc90b1df46fa2ae30a0336f4984))
* address code review — CI matrix, design doc, thread-safety comments, StreamId guard, facade subscription test ([d0367a6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d0367a609b0ea4542dc3ba01cd3653506c5bba95))
* address final review issues (position comment, Subscribe void, internal OrderPlaced, TCS in tests) ([5c05d6c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/5c05d6c08a5c7b9df1aefd43ce2d0f46d933516b))
* **inmemory:** mark _running and _disposed volatile for thread visibility ([431712c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/431712c4f3ca8425453a259f0b28e9fdb4db0647))
* **inmemory:** use volatile for _version, document ToList allocation as intentional ([1868e87](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/1868e875af3db8727eef6e44c3f51f53fb617eaa))
* resolve Meziantou analyzer warnings (MA0002, MA0006, MA0011, MA0015, MA0025, MA0051) ([d599014](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d59901437a666fcde8c19d79d4c8103e424fd129))


### Code Refactoring

* add code quality analyzers and fix warnings ([47ef3b4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/47ef3b42b734041b13b6f72a12b3920c21f40b56))
* add Meziantou and Roslynator analyzers, fix core library issues ([af68333](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/af683338ad5c0d988ecdced3805485f14011b192))
* minor post-review improvements (variable naming, dedup csproj ref, consolidate Kafka extensions) ([b5cd55a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b5cd55a3decb973ffdcd45b32813c912ea14ea02))


### Miscellaneous

* **release:** tag v1.0.0 for ZeroAlloc.EventSourcing family ([18179f6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/18179f6392e1685bbb6d974ca9642f3052910717))
