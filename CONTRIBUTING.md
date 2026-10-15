# Contributing to ZeroAlloc.EventSourcing

Thanks for helping. This file covers what you need to get a change merged.

## Prerequisites

- The .NET SDK pinned in [`global.json`](global.json): 10.0.401 or a later feature band.
- Docker, for the SQL Server, PostgreSQL and Kafka integration tests, which use Testcontainers.

## Build and test

```bash
dotnet build ZeroAlloc.EventSourcing.slnx --configuration Release
dotnet test ZeroAlloc.EventSourcing.slnx --configuration Release
```

The build treats warnings as errors. Benchmarks live in `src/ZeroAlloc.EventSourcing.Benchmarks`:

```bash
dotnet run --project src/ZeroAlloc.EventSourcing.Benchmarks -c Release
```

## Rules a change has to meet

- **Public API is tracked.** Every package has `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Add new public symbols to `PublicAPI.Unshipped.txt`; RS0016 and RS0017 fail the build otherwise. Don't remove or change a shipped signature outside a major release.
- **NativeAOT stays clean.** CI publishes and runs every `samples/*AotSmoke` project with `PublishAot=true`. No reflection, `MakeGenericType` or `Activator.CreateInstance` on runtime paths.
- **Allocation claims need a test.** If you document a path as allocation-free, or change one that is, cover it with an `AllocationGate` test from ZeroAlloc.TestHelpers that measures the whole public call with an exact budget.
- **Tests come with the change.** Bug fixes include a test that fails without the fix.

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `perf:`, `docs:`, `test:`, `refactor:`, `chore:`. Releases are cut by release-please from these messages; see [docs/releasing.md](docs/releasing.md).
- Don't nest parentheses in a commit body. release-please fails to parse such a commit and silently leaves it out of the changelog.
- Open the pull request against `main` and fill in the template. Describe any behaviour change explicitly, so it can go into the release notes.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
