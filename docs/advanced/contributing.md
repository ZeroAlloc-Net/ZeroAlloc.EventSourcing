# Contributing to ZeroAlloc.EventSourcing

The contributing guide lives in the repository, next to the code it describes:
**[CONTRIBUTING.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/CONTRIBUTING.md)**.
It is kept there so there is one copy to keep current.

It covers:

- the prerequisites: the .NET SDK pinned in `global.json`, and Docker for the SQL Server,
  PostgreSQL and Kafka integration tests;
- how to build and test the solution in Release, as CI does;
- the rules a change has to meet: tracked public API, clean NativeAOT, an `AllocationGate` test
  behind every allocation claim, and a test with every fix;
- Conventional Commits and pull requests.

## How a change ships

1. Open a pull request against `main`. CI builds and tests the solution, runs the NativeAOT
   smoke tests, and checks the public API against the last release.
2. Once it is merged, release-please reads the Conventional Commit messages on `main` and keeps a
   release pull request up to date with the next version and changelog.
3. When the maintainer merges that release pull request, the release is tagged, the full test
   suite runs again, and every package is published to NuGet at the same version.

[Releasing](../releasing.md) explains the single-version setup and why it was chosen.

## Reporting a bug or asking for a feature

Open an issue on
[GitHub](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/issues). For a bug, include the
package versions, the target framework, and the smallest code that shows the problem.

## License

ZeroAlloc.EventSourcing is licensed under the
[MIT License](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing/blob/main/LICENSE). By
contributing, you agree that your contributions are licensed under it too.
