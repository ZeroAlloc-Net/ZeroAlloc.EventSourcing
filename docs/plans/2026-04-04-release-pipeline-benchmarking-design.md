# Release Pipeline & Benchmarking Design

**Date:** 2026-04-04  
**Status:** APPROVED  
**Scope:** Unified release pipeline with comprehensive benchmarking for ZeroAlloc.EventSourcing and org-wide reuse

---

## Overview

ZeroAlloc.EventSourcing will implement a consolidated release pipeline following the ZeroAlloc.Mediator pattern. This design establishes:

1. **Reusable GitHub Actions workflows** in the `.github` org repo for all 13 NuGet packages
2. **Comprehensive benchmarking suite** tracking performance across all event sourcing scenarios
3. **Baseline tracking & regression detection** to catch performance regressions before release
4. **Automated website updates** with performance results and documentation
5. **Release-Please + GitVersion + Conventional Commits** integration for semantic versioning

This is a **template implementation** for EventSourcing; the shared workflow will be adoptable by the other 12 NuGet repos in the organization.

---

## Architecture

### Release Flow

```
Conventional commits pushed
         ↓
    CI workflow (build, test)
         ↓
Release-Please creates PR
(GitVersion calculates semver, CHANGELOG auto-generated)
         ↓
    PR merged → tag created
         ↓
Full release workflow triggered:
├─ Run benchmarks (BenchmarkDotNet)
├─ Compare against baseline
├─ Update benchmarks.json
├─ Publish to NuGet
└─ Update website (docs/performance.md)
```

### Workflow Organization

**`.github/workflows/` (org-level in ZeroAlloc-Net/.github)**
- `nuget-release.yml` — Reusable workflow for benchmark + publish (inputs: repo name, has-benchmarks flag)
- All 13 NuGet repos call this workflow on release tag

**Per-repo configuration**
- `GitVersion.yml` — Semantic versioning rules
- `release-please-config.json` — Release automation settings
- `.commitlintrc.yml` — Conventional commits enforcement
- `benchmarks.json` — Baseline results (committed to repo)

---

## Benchmarking Infrastructure

### Scope: Four Scenarios

**1. Event Store Performance**
- `EventStoreBenchmarks.cs`
- Operations: AppendEvent, ReadEvents (single stream), ReadEventsAcrossStreams
- Metrics: Mean time (ns), allocations (bytes), P95/P99

**2. Snapshot Storage Performance**
- `SnapshotStoreBenchmarks.cs`
- Operations: WriteSnapshot, ReadSnapshot
- Tests both InMemorySnapshotStore and SQL implementations (if available)
- Metrics: Read/write latency, allocation per operation

**3. Aggregate Loading & Replay**
- `AggregateLoadBenchmarks.cs`
- Operations: LoadAggregateFromEvents, LoadWithSnapshot (compare overhead)
- Scenarios: Small stream (10 events), medium (100), large (1000)
- Metrics: Total time, event application rate (events/μs), memory churn

**4. Projection Application**
- `ProjectionBenchmarks.cs`
- Operations: ApplyEvent (single), ApplyMultipleEvents, ApplyTyped (generated dispatch)
- Metrics: Per-event application time, throughput, allocation

### BenchmarkDotNet Configuration

```csharp
[SimpleJob(warmupCount: 3, targetCount: 5)]
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class EventStoreBenchmarks
{
    // Benchmarks here
}
```

**Output:** `BenchmarkResults.json` with structure:
```json
{
  "benchmarks": [
    {
      "name": "AppendEvent",
      "meanNs": 1250,
      "allocBytes": 0,
      "p95Ns": 1450,
      "p99Ns": 1650
    }
  ]
}
```

### Baseline Tracking & Regression Detection

**File:** `benchmarks.json` (committed to repo root)

```json
{
  "version": "1.0.0",
  "timestamp": "2026-04-04T00:00:00Z",
  "benchmarks": {
    "AppendEvent": { "meanNs": 1250, "allocBytes": 0 },
    "ReadSnapshot": { "meanNs": 450, "allocBytes": 0 }
  }
}
```

**Regression Detection:**
- Workflow compares current results vs. baseline
- Fails if regression > 5% on critical paths (append, read, replay)
- Warnings if regression 3-5%
- Auto-commits new baseline if no regression

**Critical paths** (must pass):
- AppendEvent
- ReadEvents
- ReadSnapshot
- LoadAggregateFromEvents
- ApplyEvent (Projection)

---

## Reusable Workflow: `nuget-release.yml`

**Location:** `.github/workflows/nuget-release.yml` (in ZeroAlloc-Net/.github)

**Trigger:** Called by release-please on version tag

**Inputs:**
```yaml
inputs:
  repo-name:
    description: "Repository name (e.g., ZeroAlloc.EventSourcing)"
    required: true
  has-benchmarks:
    description: "Whether repo has a benchmarks project"
    type: boolean
    default: false
  nuget-org:
    description: "NuGet organization (e.g., ZeroAlloc)"
    required: true
```

**Steps:**
1. Checkout code
2. Setup .NET environment
3. Run benchmarks (if `has-benchmarks: true`)
   - Execute BenchmarkDotNet runner
   - Generate `BenchmarkResults.json`
4. Compare against baseline
   - Load `benchmarks.json`
   - Calculate regressions
   - Fail if > 5% on critical paths
5. Update baseline
   - Commit new `benchmarks.json` if valid
6. Build & publish to NuGet
   - Authenticate with org token
   - Push package
7. Update website (if docs exist)
   - Generate `docs/performance.md` from benchmark results
   - Commit doc updates

---

## Website Integration

### Performance Documentation

**Auto-generated file:** `docs/performance.md`

```markdown
# Performance & Benchmarks

**Version:** 1.0.0  
**Measured:** 2026-04-04

## Event Store Operations

| Operation | Mean (ns) | Allocations | P95 (ns) | vs Previous |
|-----------|-----------|-------------|----------|-------------|
| AppendEvent | 1250 | 0 | 1450 | +2% |
| ReadEvents | 3200 | 0 | 3800 | —— |
| StreamMetadata | 850 | 0 | 950 | -1% |

## Snapshot Storage

| Operation | Mean (ns) | Allocations | P95 (ns) | vs Previous |
|-----------|-----------|-------------|----------|-------------|
| ReadSnapshot | 450 | 0 | 520 | —— |
| WriteSnapshot | 2100 | 64 | 2500 | +3% |

...
```

**Website structure:**
```
docs/
├── getting-started.md
├── concepts/
│   ├── event-store.md
│   ├── aggregates.md
│   ├── snapshots.md
│   └── projections.md
├── guides/
│   ├── setup.md
│   ├── usage.md
│   └── best-practices.md
├── performance.md              ← Auto-updated per release
└── api-reference.md
```

---

## Per-Repo Adoption

### EventSourcing: Full Implementation

**New files:**
- `src/ZeroAlloc.EventSourcing.Benchmarks/` (project)
  - `EventStoreBenchmarks.cs`
  - `SnapshotStoreBenchmarks.cs`
  - `AggregateLoadBenchmarks.cs`
  - `ProjectionBenchmarks.cs`
  - `BenchmarkRunner.cs`
- `benchmarks.json` (baseline, initially empty/populated on first run)
- `GitVersion.yml` (copy from template)
- `release-please-config.json` (copy from template)
- `.commitlintrc.yml` (copy from template)

**Config in `.csproj`:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
  </ItemGroup>
</Project>
```

### Other 12 NuGet Repos: Phased Rollout

Each repo:
1. Adds minimal config (GitVersion.yml, release-please-config.json, .commitlintrc.yml)
2. Optionally adds benchmarks project (or skips if not performance-critical)
3. Updates release workflow to call shared `nuget-release.yml`

**Estimate:** 1-2 repos per week over 6-12 weeks

---

## Configuration Files

### `GitVersion.yml` (Template)
```yaml
mode: Mainline
branches:
  main:
    increment: Patch
    prevent-increment-of-merged-branch-version: true
  feature:
    increment: Minor
commit-message-increment-pattern: '^(feat|fix|perf)(\(.+\))?!?:'
```

### `release-please-config.json` (Template)
```json
{
  "packages": {
    ".": {
      "package-name": "ZeroAlloc.EventSourcing",
      "bump-minor-pre-major": false,
      "changelog-path": "CHANGELOG.md"
    }
  },
  "changelog-sections": [
    { "type": "feat", "section": "Features" },
    { "type": "fix", "section": "Bug Fixes" },
    { "type": "perf", "section": "Performance" },
    { "type": "breaking", "section": "Breaking Changes" }
  ]
}
```

### `.commitlintrc.yml` (Template)
```yaml
extends:
  - "@commitlint/config-conventional"
rules:
  type-enum:
    - 2
    - always
    - [feat, fix, docs, style, refactor, perf, test, chore, ci, revert]
  scope-enum:
    - 2
    - always
    - [core, store, snapshot, projection, generator, sql, tests]
```

---

## Implementation Phases

### Phase 1: Shared Workflow (Week 1)
- Add `nuget-release.yml` to `.github/workflows/`
- Create configuration templates
- Document adoption guide

### Phase 2: EventSourcing Implementation (Weeks 1-2)
- Create benchmarks project
- Implement all 4 benchmark suites
- Add version & release configs
- Test full release flow (dry run)

### Phase 3: Adoption by Other Repos (Weeks 2-13)
- Rollout to 2 repos per week
- Validate workflow works end-to-end
- Collect feedback, iterate

---

## Success Criteria

- ✓ Benchmarks run successfully before every release
- ✓ Baselines updated automatically, no manual intervention
- ✓ Regressions > 5% block release, alerting team
- ✓ Performance results published to website automatically
- ✓ All 13 NuGet repos use shared workflow by end of Phase 3
- ✓ Zero duplicated pipeline code across repos

---

## Notes

- **Tool versions:** BenchmarkDotNet 0.14.0+, GitVersion 6.0+, release-please 15.0+
- **Secrets:** NuGet API token stored in org secrets, accessed by shared workflow
- **Website:** Can be static (markdown + GitHub Pages) or dynamic (.website repo)
- **Baseline management:** `benchmarks.json` is source-controlled; Git history shows performance evolution
- **Local development:** Devs can run `dotnet run -c Release` in Benchmarks project anytime

