using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

/// <summary>
/// Concrete contract tests for <see cref="InMemorySnapshotStore{TState}"/>.
/// Inherits all contract tests from <see cref="SnapshotStoreContractTests"/>.
/// </summary>
public class InMemorySnapshotStoreTests : SnapshotStoreContractTests
{
    /// <inheritdoc/>
    protected override ISnapshotStore<TestState> CreateStore() => new InMemorySnapshotStore<TestState>();
}
