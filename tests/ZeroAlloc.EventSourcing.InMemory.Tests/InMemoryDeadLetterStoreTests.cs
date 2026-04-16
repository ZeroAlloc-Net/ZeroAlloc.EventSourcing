using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

public sealed class InMemoryDeadLetterStoreTests : DeadLetterStoreContractTests
{
    protected override IDeadLetterStore CreateStore() => new InMemoryDeadLetterStore();
}
