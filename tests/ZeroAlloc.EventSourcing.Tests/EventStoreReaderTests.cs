using System.Runtime.CompilerServices;
using System.Text.Json;
using AwesomeAssertions;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// The enumeration contract of <see cref="EventStore.ReadAsync"/> and
/// <see cref="EventStore.ReadBatchAsync"/>, which return the struct <see cref="EventStreamReader"/>
/// over pooled enumeration state, see issues 504 and 512.
/// </summary>
public class EventStoreReaderTests
{
    private static readonly StreamId Stream = new("reader");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_reads_every_event_whether_the_adapter_completes_synchronously_or_not(bool yields)
    {
        var adapter = new ScriptedAdapter(yields, Raw(1), Raw(2), Raw(3));
        var store = Store(adapter);

        var events = await ToListAsync(store.ReadAsync(Stream));

        events.Select(e => e.Position.Value).Should().Equal(1, 2, 3);
        events.Select(e => ((OrderPlaced)e.Event).OrderId).Should().Equal("1", "2", "3");
        events.Should().OnlyContain(e => e.StreamId == Stream && e.Metadata.EventType == nameof(OrderPlaced));
    }

    [Fact]
    public async Task ReadAsync_skips_unknown_event_types_between_known_ones()
    {
        var adapter = new ScriptedAdapter(yields: true, Raw(1), Raw(2, "Unknown"), Raw(3, "Unknown"), Raw(4));

        var events = await ToListAsync(Store(adapter).ReadAsync(Stream));

        events.Select(e => e.Position.Value).Should().Equal(1, 4);
    }

    [Fact]
    public async Task The_same_enumerable_can_be_enumerated_twice()
    {
        var adapter = new ScriptedAdapter(yields: false, Raw(1), Raw(2));
        var read = Store(adapter).ReadAsync(Stream, new StreamPosition(0));

        var first = await ToListAsync(read);
        var second = await ToListAsync(read);

        first.Select(e => e.Position.Value).Should().Equal(1, 2);
        second.Select(e => e.Position.Value).Should().Equal(1, 2);
        adapter.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Two_enumerators_of_the_same_enumerable_are_independent()
    {
        var adapter = new ScriptedAdapter(yields: false, Raw(1), Raw(2));
        var read = Store(adapter).ReadAsync(Stream);

        await using var a = read.GetAsyncEnumerator();
        await using var b = read.GetAsyncEnumerator();

        (await a.MoveNextAsync()).Should().BeTrue();
        (await a.MoveNextAsync()).Should().BeTrue();
        (await b.MoveNextAsync()).Should().BeTrue();

        a.Current.Position.Value.Should().Be(2);
        b.Current.Position.Value.Should().Be(1);
    }

    [Fact]
    public async Task The_adapter_enumerator_is_disposed_when_its_rows_run_out()
    {
        var adapter = new ScriptedAdapter(yields: true, Raw(1));
        await using var reader = Store(adapter).ReadAsync(Stream).GetAsyncEnumerator();

        (await reader.MoveNextAsync()).Should().BeTrue();
        adapter.Disposals.Should().Be(0);

        (await reader.MoveNextAsync()).Should().BeFalse();
        adapter.Disposals.Should().Be(1, "the adapter's connection is released before the caller disposes");

        (await reader.MoveNextAsync()).Should().BeFalse();
        reader.Current.Should().Be(default(EventEnvelope));
    }

    [Fact]
    public async Task Disposing_mid_read_disposes_the_adapter_enumerator()
    {
        var adapter = new ScriptedAdapter(yields: true, Raw(1), Raw(2));
        var reader = Store(adapter).ReadAsync(Stream).GetAsyncEnumerator();

        (await reader.MoveNextAsync()).Should().BeTrue();
        await reader.DisposeAsync();

        adapter.Disposals.Should().Be(1);
        (await reader.MoveNextAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_serializer_failure_propagates_and_disposes_the_adapter_enumerator()
    {
        var adapter = new ScriptedAdapter(yields: true, Raw(1), Raw(2, "Broken"));
        var reader = Store(adapter).ReadAsync(Stream).GetAsyncEnumerator();

        (await reader.MoveNextAsync()).Should().BeTrue();
        var next = async () => await reader.MoveNextAsync();

        await next.Should().ThrowAsync<JsonException>();
        adapter.Disposals.Should().Be(1);
        (await reader.MoveNextAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task The_adapter_sees_the_read_token_and_the_enumerator_token(bool cancelRead, bool cancelEnumerator)
    {
        using var readSource = new CancellationTokenSource();
        using var enumeratorSource = new CancellationTokenSource();
        var adapter = new ScriptedAdapter(yields: false, Raw(1));

        await using var reader = Store(adapter).ReadAsync(Stream, StreamPosition.Start, readSource.Token)
            .GetAsyncEnumerator(enumeratorSource.Token);
        (await reader.MoveNextAsync()).Should().BeTrue();

        if (cancelRead)
            await readSource.CancelAsync();
        if (cancelEnumerator)
            await enumeratorSource.CancelAsync();

        adapter.LastToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task The_adapter_gets_the_read_token_alone_when_the_enumerator_has_none()
    {
        using var readSource = new CancellationTokenSource();
        var adapter = new ScriptedAdapter(yields: false, Raw(1));

        await ToListAsync(Store(adapter).ReadAsync(Stream, StreamPosition.Start, readSource.Token));

        adapter.LastToken.Should().Be(readSource.Token);
    }

    [Fact]
    public async Task ReadBatchAsync_reads_past_a_page_of_unknown_types_through_an_asynchronous_adapter()
    {
        var adapter = new ScriptedAdapter(yields: true, Raw(1, "Unknown"), Raw(2, "Unknown"), Raw(3), Raw(4), Raw(5));

        var events = await ToListAsync(Store(adapter).ReadBatchAsync(Stream, StreamPosition.Start, 2));

        events.Select(e => e.Position.Value).Should().Equal(3, 4);
        adapter.BatchReads.Should().Equal((0L, 2), (2L, 2));
        adapter.Disposals.Should().Be(2);
    }

    [Fact]
    public async Task ReadBatchAsync_fails_when_the_adapter_does_not_advance()
    {
        var adapter = new ScriptedAdapter(yields: false, Raw(1, "Unknown"), Raw(1, "Unknown")) { IgnoreFrom = true };

        var read = async () => await ToListAsync(Store(adapter).ReadBatchAsync(Stream, new StreamPosition(1), 2));

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot advance*");
    }

    /// <summary>
    /// The enumeration state is pooled, so a copy of an enumerator kept after its enumeration was
    /// disposed must not reach the read that rents the state next. It reads nothing, and its
    /// dispose leaves the other read running.
    /// </summary>
    [Fact]
    public async Task A_stale_enumerator_copy_does_not_touch_the_next_read()
    {
        var first = new ScriptedAdapter(yields: false, Raw(1), Raw(2));
        var second = new ScriptedAdapter(yields: false, Raw(10), Raw(11));

        var stale = Store(first).ReadAsync(Stream).GetAsyncEnumerator();
        var copy = stale;
        (await stale.MoveNextAsync()).Should().BeTrue();
        await stale.DisposeAsync();

        await using var live = Store(second).ReadAsync(Stream).GetAsyncEnumerator();
        (await live.MoveNextAsync()).Should().BeTrue();

        (await copy.MoveNextAsync()).Should().BeFalse();
        copy.Current.Should().Be(default(EventEnvelope));
        await copy.DisposeAsync();

        live.Current.Position.Value.Should().Be(10);
        (await live.MoveNextAsync()).Should().BeTrue();
        live.Current.Position.Value.Should().Be(11);
        second.Disposals.Should().Be(0, "the stale copy's dispose must not end the live read");
    }

    [Fact]
    public async Task Concurrent_enumerations_on_one_thread_get_their_own_state()
    {
        var adapter = new ScriptedAdapter(yields: false, Raw(1), Raw(2), Raw(3));
        var read = Store(adapter).ReadAsync(Stream);
        var positions = new List<(char, long)>();

        await foreach (var outer in read)
        {
            positions.Add(('o', outer.Position.Value));
            await foreach (var inner in read)
                positions.Add(('i', inner.Position.Value));
        }

        positions.Where(p => p.Item1 == 'o').Select(p => p.Item2).Should().Equal(1, 2, 3);
        positions.Count(p => p.Item1 == 'i').Should().Be(9);
    }

    [Fact]
    public async Task AsAsyncEnumerable_reads_the_same_events_and_can_be_enumerated_twice()
    {
        var adapter = new ScriptedAdapter(yields: true, Raw(1), Raw(2, "Unknown"), Raw(3));
        var events = Store(adapter).ReadAsync(Stream).AsAsyncEnumerable();

        (await ToListAsync(events)).Select(e => e.Position.Value).Should().Equal(1, 3);
        (await ToListAsync(events)).Select(e => e.Position.Value).Should().Equal(1, 3);
    }

    [Fact]
    public async Task WithCancellation_and_ConfigureAwait_pass_the_token_to_the_adapter()
    {
        using var source = new CancellationTokenSource();
        var adapter = new ScriptedAdapter(yields: true, Raw(1), Raw(2));
        var positions = new List<long>();

        await foreach (var e in Store(adapter).ReadAsync(Stream).WithCancellation(source.Token).ConfigureAwait(false))
            positions.Add(e.Position.Value);
        adapter.LastToken.Should().Be(source.Token);

        await foreach (var e in Store(adapter).ReadAsync(Stream).ConfigureAwait(false).WithCancellation(source.Token))
            positions.Add(e.Position.Value);
        adapter.LastToken.Should().Be(source.Token);

        positions.Should().Equal(1, 2, 1, 2);
    }

    [Fact]
    public async Task A_cancelled_enumeration_token_stops_the_read()
    {
        using var source = new CancellationTokenSource();
        var adapter = new ScriptedAdapter(yields: true, Raw(1), Raw(2), Raw(3));
        var positions = new List<long>();

        var read = async () =>
        {
            await foreach (var e in Store(adapter).ReadAsync(Stream).WithCancellation(source.Token))
            {
                positions.Add(e.Position.Value);
                await source.CancelAsync();
                adapter.LastToken.ThrowIfCancellationRequested();
            }
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
        positions.Should().Equal(1);
        adapter.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_reader_over_an_async_enumerable_reads_its_events()
    {
        var envelope = new EventEnvelope(Stream, new StreamPosition(7), new OrderPlaced("7", 7), EventMetadata.New(nameof(OrderPlaced)));
        var reader = new EventStreamReader(Once(envelope));

        (await ToListAsync(reader)).Should().Equal(envelope);
        reader.AsAsyncEnumerable().Should().NotBeNull();

        static async IAsyncEnumerable<EventEnvelope> Once(EventEnvelope e)
        {
            await Task.Yield();
            yield return e;
        }
    }

    [Fact]
    public async Task A_default_reader_reads_nothing()
    {
        (await ToListAsync(default(EventStreamReader))).Should().BeEmpty();
        (await ToListAsync(default(EventStreamReader).AsAsyncEnumerable())).Should().BeEmpty();
    }

    [Fact]
    public void A_reader_over_null_is_rejected()
    {
        var act = () => new EventStreamReader(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("source");
    }

    private static EventStore Store(IEventStoreAdapter adapter)
        => new(adapter, new JsonEventSerializer(), new BrokenAwareRegistry());

    private static RawEvent Raw(long position, string type = nameof(OrderPlaced))
    {
        var payload = type == "Broken"
            ? "not json"u8.ToArray()
            : JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced(position.ToString(System.Globalization.CultureInfo.InvariantCulture), position));
        return new RawEvent(new StreamPosition(position), type, payload, EventMetadata.New(type));
    }

    private static async Task<List<EventEnvelope>> ToListAsync(EventStreamReader source)
    {
        var list = new List<EventEnvelope>();
        await foreach (var e in source)
            list.Add(e);
        return list;
    }

    private static async Task<List<EventEnvelope>> ToListAsync(IAsyncEnumerable<EventEnvelope> source)
    {
        var list = new List<EventEnvelope>();
        await foreach (var e in source)
            list.Add(e);
        return list;
    }

    private sealed class BrokenAwareRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventType, out Type? type)
        {
            type = eventType is nameof(OrderPlaced) or "Broken" ? typeof(OrderPlaced) : null;
            return type is not null;
        }

        public string GetTypeName(Type type) => type.Name;
    }

    /// <summary>Serves fixed rows, optionally yielding before each one, and records what it was asked.</summary>
    private sealed class ScriptedAdapter(bool yields, params RawEvent[] rows) : IEventStoreAdapter
    {
        public int Reads { get; private set; }

        public int Disposals { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public List<(long From, int MaxCount)> BatchReads { get; } = [];

        public bool IgnoreFrom { get; init; }

        public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        {
            Reads++;
            LastToken = ct;
            return Serve(from, int.MaxValue);
        }

        public IAsyncEnumerable<RawEvent> ReadBatchAsync(StreamId id, StreamPosition from, int maxCount, CancellationToken ct = default)
        {
            BatchReads.Add((from.Value, maxCount));
            LastToken = ct;
            return Serve(from, maxCount);
        }

        private async IAsyncEnumerable<RawEvent> Serve(StreamPosition from, int maxCount, [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                var served = 0;
                foreach (var row in rows)
                {
                    if (!IgnoreFrom && row.Position.Value <= from.Value)
                        continue;
                    if (served++ == maxCount)
                        yield break;
                    if (yields)
                        await Task.Yield();
                    yield return row;
                }
            }
            finally
            {
                Disposals++;
            }
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
