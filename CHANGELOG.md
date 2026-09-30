# Changelog

## [1.4.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/compare/ZeroAlloc.EventSourcing-v1.3.1...ZeroAlloc.EventSourcing-v1.4.0) (2026-09-30)


### Features

* deprecate the reflection-based ReplayableProjection constructors, ZAES008 ([#435](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/435)) ([b8ec146](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b8ec146c007ffcb78dd49b3528c5bfd41bbca2c9))


### Bug Fixes

* generate nested aggregates and projections into the real type ([#418](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/418)) ([50d30da](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/50d30da3dcc7d4956133e27c5b8dd8239d1997f1)), closes [#406](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/406)
* generate once per type for aggregates split over partial declarations ([#407](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/407)) ([b466de5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/b466de5be29b1aa1dab470ab57da74e308d1f04a)), closes [#400](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/400)
* let ReplayableProjection rebuild from a starting value instead of null ([#416](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/416)) ([f8fb4bc](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/f8fb4bc8e074786998c8684046820cfcc1d33450)), closes [#413](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/413)
* rebuild ReplayableProjection through an ISerializer, AOT-safe ([#431](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/431)) ([184f02d](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/184f02d24d610d750b0ee6b5b9ae7f550f4de032)), closes [#415](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/415)
* report a failure that came before the stop, even when it is observed after it ([#436](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/436)) ([45492c0](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/45492c00d4b0413a638d9351ad7e3909c8f9bb5a)), closes [#434](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/434)
* report ZAES007 for file-local aggregates and projections ([#420](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/420)) ([96e3761](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/96e37610766b42d5db5d2fb5f1520c49ad7189d0)), closes [#417](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/417)
* retry a Kafka handler's own cancellation, then apply the error strategy ([#432](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/432)) ([253352a](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/253352a151f2bc1c12a2f110be3e4596ad69610e)), closes [#426](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/426)
* set the aggregate Id on every repository load ([#408](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/408)) ([945d245](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/945d245590187614af245b307cea1b453f44ecd1))
* stop a consumer on shutdown instead of skipping or dead-lettering the event in flight ([#427](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/427)) ([0b23ee5](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/0b23ee5c03d62d8f953b325d1da78da8c26f33c3)), closes [#422](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/422)
* stop cleanly when a store throws a non-cancellation exception on dispose ([#423](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/423)) ([59a3667](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/59a366711182c08fcb619606da9f6c214f503569))
* stop PollingEventSubscription skipping the first event of each poll cycle ([#412](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/412)) ([e760602](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/e7606028ea6b561b04b1db552a759dc980ceb603)), closes [#409](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/409)


### Documentation

* compile the remaining docs/examples and fix the non-existent APIs in the docs ([#411](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/411)) ([7fecf61](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/7fecf613625e4ddf60c5677992bb66dfc4107a73))
* fix the remaining pages that use non-existent APIs and compile their snippets ([#414](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/414)) ([16d81a6](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/16d81a6c4917519a558697b0cd10d87560d53f65))
* use only the public API in docs and compile the runnable examples ([#403](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/403)) ([e140126](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/e14012602977cd839442f5ea024d078ad797b306)), closes [#390](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/390)


### Tests

* wait for the Kafka broker to elect a controller before creating topics ([#433](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/433)) ([e5f9f97](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/commit/e5f9f9757422035fc98dc9a846d7c81a24efab36)), closes [#429](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues/429)

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
