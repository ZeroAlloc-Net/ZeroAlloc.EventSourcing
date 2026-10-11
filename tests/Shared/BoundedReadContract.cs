using System.Text;
using AwesomeAssertions;

namespace ZeroAlloc.EventSourcing.Testing;

/// <summary>
/// The contract of <see cref="IEventStoreAdapter.ReadBatchAsync"/>, run against each adapter: a
/// read returns at most maxCount events, in position order, after the exclusive bound, so
/// StreamConsumer can page through a stream one batch at a time, see #473.
/// </summary>
internal static class BoundedReadContract
{
    /// <summary>
    /// Appends five events to "bounded-a" and two to "bounded-b", interleaved, so the global stream
    /// holds seven: a1 a2 a3 b1 b2 a4 a5.
    /// </summary>
    public static async Task SeedAsync(IEventStoreAdapter adapter)
    {
        await Append(adapter, "bounded-a", 0, "a1", "a2", "a3");
        await Append(adapter, "bounded-b", 0, "b1", "b2");
        await Append(adapter, "bounded-a", 3, "a4", "a5");
    }

    public static async Task Stream_reads_return_at_most_maxCount_events_page_by_page(IEventStoreAdapter adapter)
    {
        await SeedAsync(adapter);
        var stream = new StreamId("bounded-a");

        var pages = await ReadPagesAsync(adapter, stream, maxCount: 2);

        pages.Select(p => p.Select(e => e.EventType)).Should().BeEquivalentTo(
            new[] { new[] { "a1", "a2" }, new[] { "a3", "a4" }, new[] { "a5" } },
            o => o.WithStrictOrdering());
        pages.SelectMany(p => p).Select(e => e.Position.Value).Should().Equal(1, 2, 3, 4, 5);

        var all = await ToListAsync(adapter.ReadBatchAsync(stream, StreamPosition.Start, 100));
        all.Should().HaveCount(5, "a maxCount above the stream length returns the whole stream");
    }

    public static async Task Global_reads_return_at_most_maxCount_events_page_by_page(IEventStoreAdapter adapter)
    {
        await SeedAsync(adapter);

        var pages = await ReadPagesAsync(adapter, StreamId.Global, maxCount: 3);

        pages.Select(p => p.Select(e => e.EventType)).Should().BeEquivalentTo(
            new[] { new[] { "a1", "a2", "a3" }, new[] { "b1", "b2", "a4" }, new[] { "a5" } },
            o => o.WithStrictOrdering());
        pages.SelectMany(p => p).Select(e => e.Position.Value).Should().BeInAscendingOrder();
    }

    public static void Rejects_a_maxCount_below_one(IEventStoreAdapter adapter)
    {
        FluentActions.Invoking(() => adapter.ReadBatchAsync(new StreamId("bounded-a"), StreamPosition.Start, 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Pages through a stream the way StreamConsumer does: each read starts after the last event of the previous one.</summary>
    private static async Task<List<List<RawEvent>>> ReadPagesAsync(IEventStoreAdapter adapter, StreamId id, int maxCount)
    {
        var pages = new List<List<RawEvent>>();
        var from = StreamPosition.Start;
        while (true)
        {
            var page = await ToListAsync(adapter.ReadBatchAsync(id, from, maxCount));
            page.Count.Should().BeLessThanOrEqualTo(maxCount);
            if (page.Count == 0)
                return pages;

            pages.Add(page);
            from = page[^1].Position;
        }
    }

    private static async Task Append(IEventStoreAdapter adapter, string stream, long expectedVersion, params string[] eventTypes)
    {
        var raws = new RawEvent[eventTypes.Length];
        for (var i = 0; i < raws.Length; i++)
        {
            raws[i] = new RawEvent(
                new StreamPosition(expectedVersion + i + 1),
                eventTypes[i],
                Encoding.UTF8.GetBytes("{}"),
                EventMetadata.New(eventTypes[i]));
        }

        var result = await adapter.AppendAsync(new StreamId(stream), raws.AsMemory(), new StreamPosition(expectedVersion));
        result.IsSuccess.Should().BeTrue();
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
