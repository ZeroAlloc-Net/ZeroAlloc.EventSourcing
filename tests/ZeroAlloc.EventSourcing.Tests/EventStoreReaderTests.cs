using System.Runtime.CompilerServices;
using System.Text.Json;
using AwesomeAssertions;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// The enumerable contract of <see cref="EventStore.ReadAsync"/> and
/// <see cref="EventStore.ReadBatchAsync"/>, which are hand-written readers rather than
/// compiler-generated iterators, see issue 504.
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

    private static EventStore Store(IEventStoreAdapter adapter)
        => new(adapter, new JsonEventSerializer(), new BrokenAwareRegistry());

    private static RawEvent Raw(long position, string type = nameof(OrderPlaced))
    {
        var payload = type == "Broken"
            ? "not json"u8.ToArray()
            : JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced(position.ToString(System.Globalization.CultureInfo.InvariantCulture), position));
        return new RawEvent(new StreamPosition(position), type, payload, EventMetadata.New(type));
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
