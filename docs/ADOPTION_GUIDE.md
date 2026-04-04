# Release Pipeline & Benchmarking Adoption Guide

This document describes how to adopt the unified release pipeline (including optional benchmarking) for other ZeroAlloc NuGet packages. ZeroAlloc.EventSourcing is the reference implementation.

## Architecture Overview

The release pipeline uses three components:

1. **Shared Workflow** (in `.github` org repo)
   - Location: `ZeroAlloc-Net/.github/.github/workflows/nuget-release.yml`
   - Purpose: Centralized release logic (build, test, benchmark, publish)
   - Used by: All 13 ZeroAlloc NuGet packages

2. **Per-Repo Trigger** (in your repo)
   - Location: `.github/workflows/release.yml`
   - Purpose: Calls shared workflow with repo-specific inputs
   - Triggered by: Git tags (e.g., `v1.0.0`)

3. **Per-Repo Configuration** (in your repo)
   - Files: `GitVersion.yml`, `release-please-config.json`, `.commitlintrc.yml`, `benchmarks.json`
   - Purpose: Configure versioning, changelog, commits, baselines
   - Customized by: Each repo (mostly identical templates)

## Prerequisites

- Repository is a .NET 8+ NuGet package
- GitHub Actions available in your org repo
- Access to push to your repo
- (Optional) NUGET_API_KEY secret configured in repo or org

## Quick Start: Minimal Setup (No Benchmarks)

For repos where performance tracking isn't critical:

### Step 1: Add Configuration Files

Copy these to your repo root:

**`GitVersion.yml`**
```yaml
mode: Mainline
branches:
  main:
    increment: Patch
    prevent-increment-of-merged-branch-version: true
  feature:
    increment: Minor
  release:
    increment: Patch
commit-message-increment-pattern: '^(feat|fix|perf)(\(.+\))?!?:'
```

**`release-please-config.json`** (update `package-name`)
```json
{
  "packages": {
    ".": {
      "package-name": "YOUR_PACKAGE_NAME",
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

**`.commitlintrc.yml`** (customize scopes for your repo)
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
    - [core, handler, middleware, tests]
```

**`benchmarks.json`** (if not benchmarking, leave empty)
```json
{
  "version": "0.0.0",
  "timestamp": "2026-04-04T00:00:00Z",
  "benchmarks": {}
}
```

### Step 2: Add Release Workflow

Create `.github/workflows/release.yml`:

```yaml
name: Release

on:
  push:
    tags:
      - 'v*'

jobs:
  release:
    uses: ZeroAlloc-Net/.github/.github/workflows/nuget-release.yml@main
    with:
      repo-name: 'YOUR_PACKAGE_NAME'
      has-benchmarks: false
      nuget-org: 'ZeroAlloc'
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```

### Step 3: Commit and Test

```bash
git add GitVersion.yml release-please-config.json .commitlintrc.yml benchmarks.json .github/
git commit -m "ci: add release pipeline configuration"
git push
```

**Test the setup:**
- Create a release-please PR (via `gh` or manually)
- Merge it (this creates a version tag)
- Watch the release workflow execute
- Verify NuGet package is published

## Advanced Setup: With Benchmarking

For performance-critical repos (like EventSourcing):

### Step 1: Create Benchmarks Project

Create `src/YourPackage.Benchmarks/` with:
- `YourPackage.Benchmarks.csproj` (see EventSourcing example)
- `BenchmarkConfig.cs` (copy from EventSourcing)
- `BenchmarkRunner.cs` (copy from EventSourcing)
- `CoreBenchmarks.cs`, `AdvancedBenchmarks.cs`, etc. (your benchmarks)

Add to `.csproj`:
```xml
<ItemGroup>
  <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
</ItemGroup>
```

### Step 2: Update Release Workflow

In `.github/workflows/release.yml`, set `has-benchmarks: true`:

```yaml
with:
  repo-name: 'YOUR_PACKAGE_NAME'
  has-benchmarks: true  # ← Changed to true
  nuget-org: 'ZeroAlloc'
```

### Step 3: Add Performance Documentation

Create `docs/performance.md` (copy template from EventSourcing at `docs/performance.md`)

Update the tables with your benchmark operations.

### Step 4: Verify Benchmarks

Before committing:

```bash
# Build
dotnet build -c Release

# Run benchmarks locally
dotnet run -c Release --project src/YourPackage.Benchmarks/
```

Expected: Benchmarks complete, results displayed.

## Release Process

### Automatic (Recommended)

1. **Push commits using conventional commits:**
   - `feat: add new feature` → triggers minor version bump
   - `fix: resolve bug` → triggers patch version bump
   - `perf: improve performance` → included in changelog
   - `breaking: remove API` → triggers major version bump

2. **Release-Please automatically:**
   - Creates PR with version bump and CHANGELOG
   - Propose next semantic version (using GitVersion)

3. **Merge release PR:**
   - This creates a version tag (e.g., `v1.2.0`)
   - GitHub Actions triggers release workflow automatically
   - Workflow: runs benchmarks (if enabled) → publishes to NuGet → creates release

### Manual Release (if needed)

```bash
# Create a tag manually
git tag v1.2.0
git push --tags

# Release workflow triggers automatically
```

## Customization Guide

### Changing Scope Enum

In `.commitlintrc.yml`, customize the allowed commit scopes for your repo:

```yaml
scope-enum:
  - 2
  - always
  - [core, api, handlers, tests, docs, ci]  # Your scopes here
```

### Changing Package Name

In `release-please-config.json`:

```json
"package-name": "ZeroAlloc.YourPackage"
```

And in `.github/workflows/release.yml`:

```yaml
repo-name: 'ZeroAlloc.YourPackage'
```

### Disabling Benchmarks

If you don't want benchmarking:

1. Set `has-benchmarks: false` in release workflow
2. Skip creating the Benchmarks project
3. Leave `benchmarks.json` empty
4. Skip `docs/performance.md` (or create a stub)

### Adding Performance Documentation

If you add benchmarks later:

1. Create `docs/performance.md` (see EventSourcing template)
2. Document your benchmark operations in markdown tables
3. Update `.github/workflows/release.yml`: `has-benchmarks: true`

## Secrets Configuration

### NUGET_API_KEY

Required for NuGet publishing. Set up in one of two ways:

**Per-Repo (Individual secret):**
1. Go to your repo Settings → Secrets and variables → Actions
2. Create secret: `NUGET_API_KEY` = your NuGet API key

**Organization-wide (Recommended):**
1. Go to ZeroAlloc-Net org Settings → Secrets and variables → Actions
2. Create secret: `NUGET_API_KEY` = your NuGet API key
3. All repos inherit it automatically

## Testing Your Setup

### Local Testing

```bash
# Clean build
dotnet clean && dotnet build -c Release

# Run all tests
dotnet test --configuration Release

# (Optional) Run benchmarks
dotnet run -c Release --project src/YourPackage.Benchmarks/
```

### Workflow Testing (Dry Run)

Create a test tag:
```bash
git tag v0.0.1-test
git push origin v0.0.1-test
```

Watch the Actions tab to see if the release workflow executes. Delete the tag after testing:
```bash
git tag -d v0.0.1-test
git push origin --delete v0.0.1-test
```

## Troubleshooting

### Workflow doesn't trigger on tag push

- Check: Tag matches `v*` pattern (e.g., `v1.0.0`, not just `1.0.0`)
- Check: `.github/workflows/release.yml` exists in the repo
- Check: `on: push: tags: - 'v*'` is in release.yml

### NuGet publish fails

- Check: `NUGET_API_KEY` secret is set in repo or org
- Check: API key is valid and not expired
- Check: Package name in `release-please-config.json` matches your actual package

### Benchmarks fail

- Check: `src/YourPackage.Benchmarks/` project exists
- Check: `has-benchmarks: true` in release workflow
- Check: Benchmarks compile locally: `dotnet build -c Release`

### Tests fail

- Check: All tests pass locally: `dotnet test --configuration Release`
- Check: No database/external service dependencies in unit tests

## Migration Path

### Existing Repos (Currently Manual Release)

If your repo doesn't have this pipeline yet:

1. **Backup your current release process** (document it for reference)
2. **Follow Quick Start above** (minimal setup first)
3. **Test with one release** (create a test tag, verify workflow)
4. **Switch off old release process** (retire manual steps)

### Repos Already Using release-please

If you already have release-please configured:

1. Copy `GitVersion.yml`, `.commitlintrc.yml`, `benchmarks.json` to your repo
2. Update `release-please-config.json` with your package name (keep other settings)
3. Update `.github/workflows/release.yml` to call the shared workflow (replace existing)
4. Test: Merge release PR and watch the new shared workflow execute

## Questions?

Reference the implementation in `ZeroAlloc.EventSourcing`:
- Benchmarks: `src/ZeroAlloc.EventSourcing.Benchmarks/`
- Configuration: `GitVersion.yml`, `release-please-config.json`, `.commitlintrc.yml`
- Workflow: `.github/workflows/release.yml`
- Docs: `docs/performance.md`

For shared workflow issues: See `ZeroAlloc-Net/.github/.github/workflows/nuget-release.yml`
