# Contributing to ZeroAlloc.EventSourcing

**Version:** 1.0  
**Last Updated:** 2026-04-04

## Welcome!

ZeroAlloc.EventSourcing is an open-source project, and we welcome contributions of all kinds: bug reports, feature requests, documentation, code, and tests.

This guide walks you through the contribution process.

## Getting Started

### Prerequisites

- **.NET 8.0+** (download from [dotnet.microsoft.com](https://dotnet.microsoft.com))
- **Git** (download from [git-scm.com](https://git-scm.com))
- **A code editor** (VS Code, Visual Studio, or your favorite)
- **Familiarity with event sourcing** (read [Core Concepts](../core-concepts/fundamentals.md))

### Development Environment Setup

1. **Clone the repository**

```bash
git clone https://github.com/yourusername/ZeroAlloc.EventSourcing.git
cd ZeroAlloc.EventSourcing
```

2. **Restore dependencies**

```bash
dotnet restore
```

3. **Build the project**

```bash
dotnet build
```

4. **Run tests**

```bash
dotnet test
```

All tests should pass. If they don't, check your .NET version or dependencies.

### Project Structure

```
ZeroAlloc.EventSourcing/
├── src/
│   ├── ZeroAlloc.EventSourcing/
│   │   ├── Aggregate.cs              # Base aggregate class
│   │   ├── EventStore.cs             # Event store facade
│   │   ├── IEventStore.cs            # Event store interface
│   │   ├── ISnapshotStore.cs         # Snapshot store interface
│   │   ├── IProjection.cs            # Projection interface
│   │   └── ... (other core files)
│   └── ZeroAlloc.EventSourcing.Benchmarks/
│       ├── AggregateLoadBenchmarks.cs
│       ├── EventStoreBenchmarks.cs
│       └── ... (benchmarks)
├── tests/
│   ├── ZeroAlloc.EventSourcing.Tests/
│   │   ├── AggregateTests.cs
│   │   ├── EventStoreTests.cs
│   │   └── ... (unit tests)
├── docs/
│   ├── core-concepts/
│   ├── getting-started/
│   ├── performance/
│   ├── advanced/
│   └── ... (documentation)
└── README.md
```

**Key files to understand first:**

- `Aggregate.cs` — Base class for all aggregates
- `EventStore.cs` — Event store public API
- `IEventStoreAdapter.cs` — Event store SPI (for custom implementations)
- `ISnapshotStore.cs` — Snapshot store interface

## Types of Contributions

### 1. Bug Reports

Found a bug? Help us fix it!

**Before reporting, check:**
- Is it already reported? (search [Issues](https://github.com/yourusername/ZeroAlloc.EventSourcing/issues))
- Can you reproduce it consistently?
- Does it happen with the latest version?

**When reporting:**
- Use a descriptive title ("AppendAsync throws NullReferenceException with null events")
- Include steps to reproduce
- Provide minimal code example
- State your .NET version and OS

**Example:**

```
Title: AppendAsync throws NullReferenceException

Steps:
1. Create an event store
2. Call AppendAsync(streamId, null, position)

Expected: Should throw ArgumentNullException
Actual: NullReferenceException at EventStore.cs:42

Environment:
- .NET 8.0.25
- Windows 11
```

### 2. Feature Requests

Want to suggest a new feature?

**Before requesting:**
- Check if it's already requested ([Issues](https://github.com/yourusername/ZeroAlloc.EventSourcing/issues))
- Is it in scope for ZeroAlloc.EventSourcing?
- Does it align with the zero-allocation philosophy?

**When requesting:**
- Explain the use case
- Provide concrete examples
- Discuss alternatives
- Be open to feedback

**Example:**

```
Title: Add support for event filtering in ReadAsync

Use case:
I need to read only certain event types from a stream to build 
a specialized projection. Currently, I must read all events and 
filter client-side.

Proposed API:
eventStore.ReadAsync(streamId, predicate: e => e.Type == "OrderPlaced")

Alternative considered:
Client-side filtering (works but inefficient for large streams)
```

### 3. Documentation

Documentation improvements are always welcome!

- Fix typos or unclear explanations
- Add examples
- Clarify complex concepts
- Update outdated information
- Add missing sections

**Process:**
1. Fork the repository
2. Edit Markdown files in `/docs`
3. Submit a pull request (see [PR Process](#pr-process))

### 4. Code Changes

### Bug Fixes

For bug fixes:

1. **Create a test that fails**

```csharp
[TestMethod]
[ExpectedException(typeof(ArgumentNullException))]
public async Task AppendAsync_ThrowsArgumentNullException_WhenEventsNull()
{
    var store = CreateEventStore();
    await store.AppendAsync(new StreamId("test"), null!, StreamPosition.Start);
}
```

2. **Make minimal changes to fix the bug**

3. **Verify the test passes**

4. **Submit a PR** with the test and fix

### Features

For new features:

1. **Discuss first** — Open an issue to discuss the feature
2. **Design** — Propose API design (interfaces, methods, parameters)
3. **Implement** — Write the feature code
4. **Test** — Add comprehensive tests
5. **Document** — Add documentation
6. **Submit PR** — Link to the issue

**Feature checklist:**

- [ ] New public APIs are documented
- [ ] All tests pass
- [ ] No regressions in performance
- [ ] Code follows project style
- [ ] Documentation is updated

## Coding Standards

### Code Style

```csharp
// ✓ Good: Clear, focused methods
public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
    StreamId streamId,
    ReadOnlyMemory<object> events,
    StreamPosition expectedVersion,
    CancellationToken ct = default)
{
    // Validate inputs
    if (streamId == default)
        throw new ArgumentException("StreamId required", nameof(streamId));

    if (events.Length == 0)
        throw new ArgumentException("Events required", nameof(events));

    // Implementation...
}

// ✗ Bad: Too many concerns
public async Task<object> Process(object input)
{
    // Mixing validation, I/O, and business logic
    var result = await Database.Query(input);
    // ... 100 lines of code
    return Transform(result);
}
```

### Design Principles

Follow these principles when writing code:

1. **Zero-allocation mindset** — Use structs, stack allocation, value types
2. **Async/await** — All I/O should be async
3. **Immutability** — State should be immutable
4. **Error handling** — Use Result<T, TError> pattern
5. **Separation of concerns** — Each class has one responsibility

### Naming Conventions

```csharp
// Types: PascalCase
public class EventStore { }
public interface IEventStore { }
public struct OrderState { }
public enum StreamPosition { }

// Methods: PascalCase
public async ValueTask AppendAsync(...)
public OrderState Apply(OrderPlacedEvent e)

// Properties: PascalCase
public string Name { get; }
public int Position { get; }

// Variables: camelCase
var streamId = new StreamId("order-123");
foreach (var @event in events)
{
    // ...
}

// Private fields: _camelCase or camelCase (your choice, be consistent)
private readonly string _streamId;
private int _eventCount;
```

### Comments

- **Avoid obvious comments** — Code should be self-documenting
- **Explain "why"** — Comments should explain design decisions
- **Document public APIs** — Use XML documentation comments

```csharp
// ✗ Bad: Obvious comment
var position = position + 1;  // Increment position

// ✓ Good: Explains "why"
// Increment position because we're moving to the next event in the stream
var nextPosition = position.Next();

// ✓ Good: XML doc
/// <summary>
/// Appends events to a stream, ensuring optimistic locking via position.
/// Returns conflict if the expected version doesn't match the current position.
/// </summary>
/// <remarks>
/// This method is atomic: either all events are appended or none are.
/// It uses position-based optimistic locking to prevent concurrent modifications.
/// </remarks>
public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(...)
```

## Testing

### Test Organization

```csharp
[TestClass]
public class EventStoreTests
{
    private IEventStore _store;
    private StreamId _streamId;

    [TestInitialize]
    public void Setup()
    {
        _store = new InMemoryEventStore();
        _streamId = new StreamId("test-stream");
    }

    [TestMethod]
    public async Task AppendAsync_AppendsEvent()
    {
        // Arrange
        var @event = new TestEvent();

        // Act
        var result = await _store.AppendAsync(_streamId, new[] { @event }, StreamPosition.Start);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(new StreamPosition(0), result.Value.FirstPosition);
    }
}
```

### Test Naming

Use descriptive names that explain what is being tested and what the expected behavior is:

```csharp
[TestMethod]
public async Task AppendAsync_ReturnsConflict_WhenVersionMismatches()
{
    // Test pattern: Method_Behavior_Condition
}

[TestMethod]
public async Task ReadAsync_ReturnsAllEvents_WhenReadFromStart()
{
    // Arrange, Act, Assert pattern
}
```

### Testing Coverage

Aim for:
- **Happy path** — Normal operation works
- **Error cases** — Invalid inputs are handled
- **Edge cases** — Boundary conditions
- **Concurrency** — Multiple threads/tasks

## PR Process

### Before You Start

1. **Check open issues** — Is someone already working on this?
2. **Discussion** — For new features, open an issue first
3. **Create a branch** — `git checkout -b fix/issue-123` or `feature/add-snapshots`

### When Submitting a PR

1. **Fork the repository** (if you don't have commit access)

2. **Create a feature branch**

```bash
git checkout -b feature/my-feature
```

3. **Make your changes** with clear commit messages

```bash
git add .
git commit -m "feat: add snapshot rebuild capability"
```

Use conventional commits:
- `feat:` — New feature
- `fix:` — Bug fix
- `docs:` — Documentation
- `test:` — Tests
- `perf:` — Performance improvement
- `refactor:` — Code refactoring

4. **Run tests locally**

```bash
dotnet test
```

5. **Run benchmarks (if performance-sensitive)**

```bash
cd src/ZeroAlloc.EventSourcing.Benchmarks
dotnet run -c Release
```

6. **Push and create PR**

```bash
git push origin feature/my-feature
```

Then create a PR on GitHub with:
- Clear title and description
- Link to related issues (`Fixes #123`)
- Summary of changes
- Testing performed

### PR Checklist

- [ ] Tests added/updated
- [ ] Documentation added/updated
- [ ] Code follows style guide
- [ ] No performance regressions
- [ ] Commit messages are clear
- [ ] Related issues are linked

### Code Review Process

1. **At least 2 approvals** required before merge
2. **All tests must pass** (GitHub Actions)
3. **No merge conflicts** with main branch
4. **Feedback addressed** before merge

### After Merge

- Delete your feature branch
- Your contribution is now part of the project!
- Monitor the discussion in case of questions

## Release Process

Releases follow semantic versioning: `MAJOR.MINOR.PATCH`

- **MAJOR** — Breaking changes (e.g., API redesign)
- **MINOR** — New features, backward compatible
- **PATCH** — Bug fixes, backward compatible

### Release Checklist

- [ ] All tests pass
- [ ] Benchmarks show no regressions
- [ ] Documentation updated
- [ ] CHANGELOG.md updated
- [ ] Version number bumped
- [ ] Release notes written
- [ ] NuGet package published

## Performance Considerations

When contributing code:

1. **Avoid allocations** — Use structs, stack allocation
2. **Benchmark changes** — Run benchmarks before/after
3. **Profile if needed** — Use dotTrace or similar tools
4. **Consider scaling** — How does it perform with 1M events?

## Common Pitfalls

### ✗ Don't

- Modify the public API without discussion
- Add dependencies without justification
- Change behavior in ways that break existing code
- Ignore test failures
- Commit large unrelated changes

### ✓ Do

- Write clear commit messages
- Add tests for new code
- Update documentation
- Ask questions if unsure
- Start small and iterate

## Tools and Workflow

### Building

```bash
# Restore dependencies
dotnet restore

# Debug build
dotnet build

# Release build (optimized, benchmarks)
dotnet build -c Release
```

### Testing

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter TestClass=EventStoreTests

# Run with coverage
dotnet test /p:CollectCoverage=true
```

### Benchmarking

```bash
cd src/ZeroAlloc.EventSourcing.Benchmarks
dotnet run -c Release -- --benchmark-name *AggregateLoad*
```

### Code Analysis

```bash
# Analyze code for issues
dotnet build /p:EnforceCodeStyleInBuild=true

# Format code
dotnet format
```

## Getting Help

- **Questions?** Open a [Discussion](https://github.com/yourusername/ZeroAlloc.EventSourcing/discussions)
- **Issues?** Check [existing issues](https://github.com/yourusername/ZeroAlloc.EventSourcing/issues)
- **Documentation?** See `/docs` folder
- **Contact?** Email or Discord

## Resources

- [Event Sourcing Fundamentals](../core-concepts/fundamentals.md)
- [Architecture Guide](../core-concepts/architecture.md)
- [Performance Guide](../performance/characteristics.md)
- [Contributing Examples](../examples/)

## License

By contributing, you agree that your contributions will be licensed under the same license as the project (typically MIT or Apache 2.0).

## Code of Conduct

We expect all contributors to:
- Be respectful and inclusive
- Assume good faith
- Focus on the code, not the person
- Help others learn and grow

## Recognition

Contributors are recognized in:
- Commit history
- Release notes
- Contributors file
- GitHub insights

Thank you for contributing to ZeroAlloc.EventSourcing!

## Quick Reference

**Common tasks:**

```bash
# Setup
git clone https://github.com/yourusername/ZeroAlloc.EventSourcing.git
cd ZeroAlloc.EventSourcing
dotnet restore
dotnet build
dotnet test

# Make a change
git checkout -b feature/my-feature
# ... edit files ...
dotnet test
dotnet format
git add .
git commit -m "feat: my feature"
git push origin feature/my-feature

# Create PR on GitHub
# ... wait for review ...
# ... address feedback ...
# ... PR is merged! ...

# Cleanup
git checkout main
git pull
git branch -d feature/my-feature
```

Happy contributing!
