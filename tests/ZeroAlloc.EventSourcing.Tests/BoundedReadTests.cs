using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// The bounded reads behind <see cref="StreamConsumer"/>: each batch reads at most BatchSize
/// events from the store, and the store bounds the query the adapter runs, see #473.
/// </summary>
public class BoundedReadTests
{
    private static readonly StreamId Stream = new("bounded");

    [Fact]
    public async Task Consumer_reads_each_batch_through_the_bounded_read_with_BatchSize()
    {
        var adapter = new RecordingAdapter(new InMemoryEventStoreAdapter());
        var store = new EventStore(adapter, new JsonEventSerializer(), new StreamConsumerTestEventTypeRegistry());
        for (var i = 1; i <= 5; i++)
            await store.AppendAsync(Stream, new object[] { new TestEvent { Value = i } }.AsMemory(), new StreamPosition(i - 1));

        var consumer = new StreamConsumer(
            store, new InMemoryCheckpointStore(), "bounded-consumer",
            new StreamConsumerOptions { BatchSize = 2 }, Stream);
        var processed = new List<int>();

        await consumer.ConsumeAsync((envelope, _) =>
        {
            processed.Add(((TestEvent)envelope.Event).Value);
            return Task.CompletedTask;
        });

        processed.Should().Equal(1, 2, 3, 4, 5);
        adapter.UnboundedReads.Should().Be(0, "every batch must bound the query the adapter runs");
        adapter.BoundedReads.Should().NotBeEmpty();
        adapter.BoundedReads.Should().OnlyContain(r => r.MaxCount == 2 && r.Rows <= 2);
    }

    [Fact]
    public async Task Consumer_does_not_stop_on_a_batch_of_unknown_event_types()
    {
        var inner = new InMemoryEventStoreAdapter();
        var store = new EventStore(inner, new JsonEventSerializer(), new StreamConsumerTestEventTypeRegistry());
        await inner.AppendAsync(
            Stream, new[] { Raw("Unknown", 1), Raw("Unknown", 2), Raw("Unknown", 3) }.AsMemory(), StreamPosition.Start);
        await store.AppendAsync(Stream, new object[] { new TestEvent { Value = 7 } }.AsMemory(), new StreamPosition(3));

        var consumer = new StreamConsumer(
            store, new InMemoryCheckpointStore(), "unknown-consumer",
            new StreamConsumerOptions { BatchSize = 2 }, Stream);
        var processed = new List<int>();

        await consumer.ConsumeAsync((envelope, _) =>
        {
            processed.Add(((TestEvent)envelope.Event).Value);
            return Task.CompletedTask;
        });

        processed.Should().Equal(7);
    }

    [Fact]
    public async Task EventStore_ReadBatchAsync_returns_at_most_maxCount_and_resumes_after_from()
    {
        var store = new EventStore(new InMemoryEventStoreAdapter(), new JsonEventSerializer(), new StreamConsumerTestEventTypeRegistry());
        for (var i = 1; i <= 5; i++)
            await store.AppendAsync(Stream, new object[] { new TestEvent { Value = i } }.AsMemory(), new StreamPosition(i - 1));

        var first = await ToListAsync(store.ReadBatchAsync(Stream, StreamPosition.Start, 3));
        first.Select(e => e.Position.Value).Should().Equal(1, 2, 3);

        var rest = await ToListAsync(store.ReadBatchAsync(Stream, first[^1].Position, 3));
        rest.Select(e => e.Position.Value).Should().Equal(4, 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadBatchAsync_rejects_a_maxCount_below_one_when_called(int maxCount)
    {
        var adapter = new UnboundedOnlyAdapter(3);
        IEventStore store = new EventStore(adapter, new JsonEventSerializer(), new StreamConsumerTestEventTypeRegistry());
        IEventStore fallbackStore = new UnboundedOnlyStore(3);

        FluentActions.Invoking(() => store.ReadBatchAsync(Stream, StreamPosition.Start, maxCount))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ((IEventStoreAdapter)adapter).ReadBatchAsync(Stream, StreamPosition.Start, maxCount))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => fallbackStore.ReadBatchAsync(Stream, StreamPosition.Start, maxCount))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Adapter_default_ReadBatchAsync_stops_the_unbounded_read_at_maxCount()
    {
        var adapter = new UnboundedOnlyAdapter(10);

        var read = await ToListAsync(((IEventStoreAdapter)adapter).ReadBatchAsync(Stream, StreamPosition.Start, 4));

        read.Select(e => e.Position.Value).Should().Equal(1, 2, 3, 4);
        adapter.RowsYielded.Should().Be(4, "the default implementation must not pull rows past maxCount");
    }

    [Fact]
    public async Task Store_default_ReadBatchAsync_stops_the_unbounded_read_at_maxCount()
    {
        IEventStore store = new UnboundedOnlyStore(10);

        var read = await ToListAsync(store.ReadBatchAsync(Stream, new StreamPosition(2), 3));

        read.Select(e => e.Position.Value).Should().Equal(3, 4, 5);
    }

    private static RawEvent Raw(string eventType, long position)
        => new(new StreamPosition(position), eventType, Encoding.UTF8.GetBytes("{}"), EventMetadata.New(eventType));

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    /// <summary>Records which read the store issues, and how many rows each bounded read returned.</summary>
    private sealed class RecordingAdapter(IEventStoreAdapter inner) : IEventStoreAdapter
    {
        public int UnboundedReads { get; private set; }

        public List<(int MaxCount, int Rows)> BoundedReads { get; } = [];

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => inner.AppendAsync(id, events, expectedVersion, ct);

        public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        {
            UnboundedReads++;
            return inner.ReadAsync(id, from, ct);
        }

        public async IAsyncEnumerable<RawEvent> ReadBatchAsync(
            StreamId id, StreamPosition from, int maxCount, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = BoundedReads.Count;
            BoundedReads.Add((maxCount, 0));
            await foreach (var raw in inner.ReadBatchAsync(id, from, maxCount, ct))
            {
                BoundedReads[index] = (maxCount, BoundedReads[index].Rows + 1);
                yield return raw;
            }
        }

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => inner.SubscribeAsync(id, from, handler, ct);
    }

    /// <summary>An adapter that implements only the unbounded read, as adapters written before #473 do.</summary>
    private sealed class UnboundedOnlyAdapter(int count) : IEventStoreAdapter
    {
        public int RowsYielded { get; private set; }

        public async IAsyncEnumerable<RawEvent> ReadAsync(
            StreamId id, StreamPosition from, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            for (var i = from.Value + 1; i <= count; i++)
            {
                RowsYielded++;
                yield return Raw(nameof(TestEvent), i);
            }
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A store that implements only the unbounded read, as stores written before #473 do.</summary>
    private sealed class UnboundedOnlyStore(int count) : IEventStore
    {
        public async IAsyncEnumerable<EventEnvelope> ReadAsync(
            StreamId id, StreamPosition from = default, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            for (var i = from.Value + 1; i <= count; i++)
                yield return new EventEnvelope(id, new StreamPosition(i), new TestEvent { Value = (int)i }, EventMetadata.New(nameof(TestEvent)));
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<object> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<EventEnvelope, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
