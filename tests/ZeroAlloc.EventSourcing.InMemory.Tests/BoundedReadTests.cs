using ZeroAlloc.EventSourcing.Testing;

namespace ZeroAlloc.EventSourcing.InMemory.Tests;

/// <summary><see cref="InMemoryEventStoreAdapter"/> against the bounded read contract, see #473.</summary>
public sealed class BoundedReadTests
{
    [Fact]
    public Task Stream_reads_return_at_most_maxCount_events()
        => BoundedReadContract.Stream_reads_return_at_most_maxCount_events_page_by_page(new InMemoryEventStoreAdapter());

    [Fact]
    public Task Global_reads_return_at_most_maxCount_events()
        => BoundedReadContract.Global_reads_return_at_most_maxCount_events_page_by_page(new InMemoryEventStoreAdapter());

    [Fact]
    public void Rejects_a_maxCount_below_one()
        => BoundedReadContract.Rejects_a_maxCount_below_one(new InMemoryEventStoreAdapter());
}
