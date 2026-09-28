# Releasing

This repo uses release-please with a **single component** at the repository root. One version
covers all twelve published packages, and the publish job packs every project under `src/` that is
not explicitly `<IsPackable>false</IsPackable>`.

This matches the majority of ZeroAlloc repos.

## Why not one component per package

It used to declare twelve components, one per `src/` package. That caused two defects.

### 1. Commits that released nothing

release-please attributes each commit to a package **by the path of the files it changed**. A
commit touching no file under any package path mapped to nothing: every component reported
`Considering: 0 commits`, no release PR opened, and the workflow still went green.

Two directories were never declared, so changes there released nothing:

- `src/ZeroAlloc.EventSourcing.Mediator.Generator` — the generator ships inside
  `ZeroAlloc.EventSourcing.Mediator`.
- `src/ZeroAlloc.EventSourcing.Benchmarks` — not published, so harmless.

Root files had the same problem. With central package management **every dependency bump lives in
a root file no component path can see**, so a dependency fix would merge green and ship nothing.
`ZeroAlloc.Saga` lost a release exactly this way; see that repo's #131 and #98.

### 2. Siblings published wrong dependency versions

Invisible unless you read a published nuspec. The old publish job packed each released package
with `-p:PackageVersion=$version`, a **global** MSBuild property, so the sibling's own version was
stamped onto every project in the graph — including the `ZeroAlloc.EventSourcing` ProjectReference.

Live on NuGet before this change:

| package | version | declared dependency |
| --- | --- | --- |
| `ZeroAlloc.EventSourcing.Aggregates` | 1.0.0 | `ZeroAlloc.EventSourcing >= 1.0.0` |
| `ZeroAlloc.EventSourcing.SqlServer` | 1.1.0 | `ZeroAlloc.EventSourcing >= 1.1.0` |
| `ZeroAlloc.EventSourcing.InMemory` | 1.2.0 | `ZeroAlloc.EventSourcing >= 1.2.0` |
| `ZeroAlloc.EventSourcing.Mediator` | 1.0.1 | `ZeroAlloc.EventSourcing >= 1.0.1` |

Each names its **own** version rather than the version it was built against. The `.Mediator` row is
the worst: **core 1.0.1 does not exist** — published core versions are 1.0.0, 1.1.0 and 1.2.0 — so
NuGet resolved the range upward to 1.1.0, silently binding consumers to a core the package was
never built or tested against.

A single version makes this correct by construction: a package at X depends on
`ZeroAlloc.EventSourcing` X, because they are the same X.

## The version floor

Collapsing set the version to `1.2.0`, the highest any package had reached, so nothing moved
backwards on NuGet. The packages that were at 1.0.0 and 1.1.0 jump; those lines are closed.

## Cost

Lockstep versioning: a fix in any package bumps all twelve. In exchange, inter-package
dependencies are always correct and every commit can release.

## Verify against NuGet, not against a green tick

Producing no packages is now a hard error, but the final check still belongs to you:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/zeroalloc.eventsourcing/index.json
curl -s https://api.nuget.org/v3-flatcontainer/zeroalloc.eventsourcing.mediator/<version>/zeroalloc.eventsourcing.mediator.nuspec \
  | grep '<dependency'
```

## Release tracking

`src/ZeroAlloc.EventSourcing.Mediator.Generator/AnalyzerReleases.Shipped.md` records the release
each analyzer rule first shipped in, and any later change to its category or severity. ZESM001 sits
under `Release 1.0.0`, the version of `ZeroAlloc.EventSourcing.Mediator` that first carried it,
before the packages were collapsed onto one version; later sections use the repository version. A
new rule goes into `AnalyzerReleases.Unshipped.md`. Changing a shipped rule's severity or category,
or removing it, has to be declared there under `### Changed Rules` or `### Removed Rules`, or the
build fails. The same move covers every `PublicAPI.Unshipped.txt`: new public API goes there, and
removing shipped API is declared with a `*REMOVED*` line.

Nobody moves entries by hand. When release-please opens or updates the release PR, the
`ship-release-tracking` job in `.github/workflows/release-please.yml` moves everything unshipped into
the Shipped files on that branch, in a `chore: mark analyzer rules and public api shipped in
<version>` commit. The `release-tracking` job in CI fails a release PR while anything is still
unshipped. Both use the shared
[`ship-release-tracking.py`](https://github.com/ZeroAlloc-Net/.github/blob/main/scripts/ship-release-tracking.py).
**Before merging a release PR,** check that it has that commit. If it doesn't, run the script with the
release version from the root of the release branch and push the result.
