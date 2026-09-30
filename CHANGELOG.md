# Changelog

## [1.3.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.3.0...ZeroAlloc.EventSourcing-v1.3.1) (2026-09-30)


### Bug Fixes

* return the event object from the SQL dead-letter stores ([#398](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/398)) ([11915fe](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/11915fe1d976199a672c653cf54542fc281ea949))

## [1.3.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.2.5...ZeroAlloc.EventSourcing-v1.3.0) (2026-09-28)


### Features

* add Aggregate.RestoreState so snapshot loads work outside the library ([b7170c6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b7170c6b563b8568a927ccd792ad3f8e0915b871))
* add RS0026-clean overloads and deprecate the optional-parameter shapes ([0f642d3](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0f642d359a2a8575bcc9c4b3ef167aa84eb7121a))
* register snapshot stores per state type so they resolve under NativeAOT ([8e4e32e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8e4e32ec5f5954c279da1a17d439bc1eb1f5d465))


### Bug Fixes

* keep event metadata in the SQL Server and PostgreSQL dead-letter stores ([afd1231](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/afd123170518ccc1e2263fda7e1d807d4e7cb9b4))
* mark released analyzer rules and public api as shipped and automate the move ([#378](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/378)) ([293842d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/293842d920f4d5dbd7f205b23b95aade9a339a54))
* stop PostgreSQL health checks leaving a connection open on every run ([0f642d3](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0f642d359a2a8575bcc9c4b3ef167aa84eb7121a))
* store SQL Server checkpoint, projection, snapshot and dead-letter ids as NVARCHAR ([#386](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/386)) ([c85028c](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c85028c3115d96a6ecc590507c0d4ba04367e16d)), closes [#384](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/384)
* ValidateAndReplay no longer discards a snapshot taken at the head of the stream ([b7170c6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b7170c6b563b8568a927ccd792ad3f8e0915b871))


### Tests

* cover value types in every NativeAOT smoke ([8e4e32e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/8e4e32ec5f5954c279da1a17d439bc1eb1f5d465))

## [1.2.5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.2.4...ZeroAlloc.EventSourcing-v1.2.5) (2026-09-26)


### Bug Fixes

* embed caught exceptions as innerException ([#362](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/362)) ([98529df](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/98529dfb67a62eafd6ad8552ac2a7a5e5f62c116))

## [1.2.4](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.2.3...ZeroAlloc.EventSourcing-v1.2.4) (2026-09-20)


### Bug Fixes

* **ci:** pin the SDK floor at the .NET 10 GA band, not the newest patch ([#332](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/332)) ([a8f4c23](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/a8f4c23a7bcf82dd5eac66d6a6da60860b58a99e))

## [1.2.3](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.2.2...ZeroAlloc.EventSourcing-v1.2.3) (2026-09-20)


### Bug Fixes

* stamp the assembly with the release version ([#326](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/326)) ([e45726d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/e45726deabf44a8ef7ce8165cd5053e9e171ebea))

## [1.2.2](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.2.1...ZeroAlloc.EventSourcing-v1.2.2) (2026-09-20)


### Bug Fixes

* declare current sibling package versions ([#324](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/324)) ([c19c564](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c19c5641b4094c5eafa6cf7e7b46317c2b042738))

## [1.2.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.2.0...ZeroAlloc.EventSourcing-v1.2.1) (2026-09-19)


### Bug Fixes

* **ci:** stamp the assembly version when publishing from a manifest ([#316](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/316)) ([11e76c0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/11e76c0db2847f5ab2b68ecd71e2292b178c7b78))
* collapse to a single release-please component so every commit can release ([#317](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/317)) ([dbcc321](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/dbcc32137f581de7ad414d9b9c8d3707420611da))
* **deps:** NSubstitute 6 — null-guard Arg.Is matchers in the Kafka tests ([#215](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/215)) ([c2ba61a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/c2ba61a93c3b5af49670ba6d0337e0833f55e78a))
* **deps:** pin SQLitePCLRaw.lib.e_sqlite3 to 3.50.3 (CVE-2025-6965) ([#198](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/198)) ([9eec76e](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/9eec76ed7bbc977bd20ee47781f31b0b6f712007))
* **slnx:** include Outbox + Sqlite project entries so release publish doesn't NETSDK1004 ([#194](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/194)) ([d9b7dcd](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/d9b7dcdceffef5bb223654de34a168e29dce39a5))
