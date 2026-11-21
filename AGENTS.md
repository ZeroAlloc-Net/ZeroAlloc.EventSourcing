# AGENTS.md: ZeroAlloc.EventSourcing

Event sourcing for .NET with source-generated aggregates. The core, the Aggregates package and the
Generators ship as NuGet packages, together with storage adapters (InMemory, SQLite, PostgreSQL, SQL Server),
a Kafka consumer, an outbox, Mediator integration and telemetry. The write path allocates nothing of its
own and everything runs under NativeAOT.

## Layout
- `src/` holds the packable libraries plus `ZeroAlloc.EventSourcing.Benchmarks`, which is not packed.
  The two generator projects (`Generators`, `Mediator.Generator`) target `netstandard2.0`, because they
  load into the compiler. Everything else multi-targets `net10.0;net11.0` from
  `Directory.Build.props`.
- `tests/` projects override `TargetFrameworks` with `net10.0;net11.0`. `Aggregates.ConsumerTests`
  has no `InternalsVisibleTo` and compiles every C# file under `docs/examples` against the public API only.
- `samples/*AotSmoke` are NativeAOT apps on `net11.0` that CI publishes and runs. They are not examples.
- `Directory.Packages.props` pins versions centrally, including transitive pins that patch vulnerable
  SQLite, SqlClient and Pkcs dependencies. Each pin carries a comment saying when to remove it.

## Build and test
Prerequisites: the SDK from `global.json` (11.0.100, roll forward to the latest feature band). Docker for the
PostgreSQL, SQL Server and Kafka tests, which start their own containers through Testcontainers. Test
projects target `net10.0` and `net11.0`, so running a test project needs the .NET 10 runtime installed
next to the SDK.

CI job `build` in `.github/workflows/ci.yml`:
```bash
dotnet restore ZeroAlloc.EventSourcing.slnx
dotnet build ZeroAlloc.EventSourcing.slnx --no-restore --configuration Release -p:Version=0.0.0-dryrun
dotnet test ZeroAlloc.EventSourcing.slnx --no-build --configuration Release --logger trx --results-directory ./TestResults --collect "XPlat Code Coverage" --settings coverage.runsettings
```
The same job dry-run packs every packable `src/*/*.csproj` with `dotnet pack "$csproj" -c Release --no-build -p:Version=0.0.0-dryrun -o ./nupkg-dryrun`,
then runs `./scripts/verify-generator-pack-layout.sh ./nupkg-dryrun`.

CI job `aot-smoke` publishes and runs each smoke, for example:
```bash
dotnet publish samples/ZeroAlloc.EventSourcing.AotSmoke/ZeroAlloc.EventSourcing.AotSmoke.csproj -r linux-x64 -c Release -o ./aot-out/AotSmoke
```
It needs clang and zlib1g-dev. The PostgreSql, SqlServer, Sql and Kafka smokes fail unless
`ZA_ES_AOTSMOKE_POSTGRES`, `ZA_ES_AOTSMOKE_SQLSERVER` and `ZA_ES_AOTSMOKE_KAFKA` point at a running server.

Fast inner loop, no Docker needed (verified):
```bash
dotnet test tests/ZeroAlloc.EventSourcing.InMemory.Tests -c Release -f net11.0
```

## Conventions and gotchas
- Warnings are errors. `RS0016` and `RS0017` are forced errors even if `TreatWarningsAsErrors` is turned
  off. A new public symbol goes in that package's `PublicAPI.Unshipped.txt`. Do not edit
  `PublicAPI.Shipped.txt` outside a release; the release PR moves entries (job `release-tracking`).
  The generators track diagnostics in `AnalyzerReleases.Unshipped.md` the same way.
- `dotnet pack` fails without `-p:Version` (`ZAVERSION001`). With `--no-build`, the preceding build needs the
  same `-p:Version` too, or the assembly version mismatches (`ZAVERSION002`).
- A path documented as allocation-free needs an `AllocationGate` test from ZeroAlloc.TestHelpers that
  measures the whole public call with an exact budget. See `EventStoreAllocationTests`. Bug fixes need a
  test that fails without the fix.
- No reflection, `MakeGenericType` or `Activator.CreateInstance` on runtime paths. Event dispatch and the
  type registry are generated at compile time. Each AotSmoke publishes with warnings as errors, so any
  trim or AOT warning fails CI.
- Coverage is gated in CI. Hits are collected with `--collect "XPlat Code Coverage"` plus
  `coverage.runsettings`; the settings file alone loses hits at random. Samples, tests and benchmarks are
  excluded in `codecov.yml`.
- `api-compat.yml` needs SDK 10 and 11 installed. Adding a project that needs a new SDK means editing it.

<!-- org-core:start sha256=8faf417fa86f11bd8ad9bc98dfa4683fba44e440291e84c6cb9b0ec3ca0c0e7e (generated from docker-nuc scripts/agents-md/org-core-zeroalloc.md; do not edit here) -->
## Org rules

These rules hold in every ZeroAlloc-Net repo. Each library makes two promises: it works under
NativeAOT and trimming, and its hot path allocates nothing. The 2026-10-11 sweep found 16 of 28 repos
allocating on the hot path, most with docs claiming 0 B.

### Hot path

The operation a consumer runs repeatedly: `Send`, a cache hit, `Validate`, `Map`, `Serialize`, a
resilience call, a state transition, an append, a worker poll, including the idle poll. Startup and
registration are not hot path. For a source generator, the hot path is the code it emits: judge the
snapshots and the generated output.

### Allocation traps the sweep found

| Pattern | Instead |
|---|---|
| `async ValueTask` that only awaits and returns | `return new ValueTask<T>(inner.XAsync(ct));` when the method only forwards a `Task<T>` (return a forwarded `ValueTask<T>` directly). The wrapper stores a synchronous throw in the returned task; the direct return lets it, and the cancellation status of the callee, reach the caller. Keep the `async` wrapper when callers depend on either |
| `Task.WhenAll` over tasks that already completed | skip the await only when every task has `IsCompletedSuccessfully` (`Status == TaskStatus.RanToCompletion` on `netstandard2.0`); otherwise `await Task.WhenAll`, which also reports faults of completed tasks |
| `TagList` or log `params object[]` built before `Enabled`/`IsEnabled` | check first, or `LoggerMessage.Define` |
| event args allocated before checking for subscribers | copy the handler to a local, check, then allocate |
| `new Serializer()` / `new Utf8JsonWriter()` per call | cache stateless instances, reuse writers with `Reset` |
| `GetString()` / `ToString()` round-trips in JSON converters | parse and format through spans |
| `ToArray()` / `ToList()` copies between layers | pass `ReadOnlyMemory<T>` or spans, copy once at most |
| LINQ in emitted code | emit `for` loops |
| `yield` iterator behind a public `GetEnumerator` | public struct enumerator, from the first release |
| `Enum.IsDefined(typeof(E), v)` with a boxed value, `decimal.GetBits(v)` to read the scale | `Enum.IsDefined<E>(v)` when `v` has type `E` (.NET 5 or later, so not on .NET Framework, `netstandard2.0`, `netstandard2.1` or .NET Core 3.1); `v.Scale` for a scale read only (.NET 7 or later, never for the coefficient or sign). Keep the old call on older targets |
| configuration parsed or read per call | parse once and cache |
| an `AsyncLocal` write per call | pass state explicitly |
| a capturing lambda or method-group delegate per call | cache the delegate in a field, or a static lambda |
| a per-key object that is never removed | remove on release; otherwise it is a leak |

Inherent allocations are fine when documented: the consumer's own result, an exception on a failure
path, a one-time cache fill.

### Tests and benchmarks

- Every hot path has an allocation gate test (`AllocationGate` from ZeroAlloc.TestHelpers) that measures
  the whole public call, including any state setup.
- Budgets are exact. If a path allocates, assert that number and comment what it is.
- Test the generated code, not a hand-written stand-in, and cover every shape: each attribute, nested
  collections, and the asynchronous case with a fake that returns pending tasks.
- Benchmarks run in CI through the shared `benchmark-smoke.yml` and target the library's LTS
  frameworks. The benchmark body allocates nothing besides the library itself. Run locally with
  `--inProcess`.
- Publish exact allocations and same-run ratios against a `Baseline`, never absolute timings. Run with
  `--launchCount 3`, pin CPUs with `taskset`, give the spread, and label every table.

### NativeAOT and trimming

- Packable runtime projects, and projects consumers publish, set `IsAotCompatible=true` on modern
  target frameworks. Analyzers and source generators target `netstandard2.0`.
- No reflection or runtime code generation on a supported path. An opt-in reflection fallback carries
  honest `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. Every suppression must be true;
  remove dead ones.
- The AOT smoke sample publishes with `TreatWarningsAsErrors=true`. If a third-party warning really
  blocks it, use a narrow, justified suppression for that warning. `WarningsNotAsErrors` selects codes,
  not assemblies, so it would also downgrade the same warning from generated code.
- The smoke runs the hot path of every package in the repo. A package without a smoke has unknown AOT
  status. Adapters run against real services in CI service containers; publish-only is a last resort.
  App and template smokes make authenticated requests.

### Docs and shipping

- Docs match measurements. After a hot-path change, re-measure and update `docs/performance.md` and the
  README. Never write "0 B" without a test that asserts it.
- New public API is `feat:`, even for a performance change. Public API changes are additive: never add
  an optional parameter to a shipped signature. A breaking end state goes in a `next-major` issue.
- A behaviour change gets a `## Behaviour change` section in the PR body.
- Conventional Commits with release-please. Subjects start lowercase, body lines stay within 100
  characters, and a body never contains nested parentheses: release-please drops the whole commit.
- Local packs use `9.9.9-local.<sha>`. After a release, verify the package on NuGet.
- Fix review findings in the PR; file an issue only for what cannot go in it.

Full rules, template wiring lessons and the reasons behind each rule: https://github.com/ZeroAlloc-Net/.github/blob/main/AGENTS.md
<!-- org-core:end -->
