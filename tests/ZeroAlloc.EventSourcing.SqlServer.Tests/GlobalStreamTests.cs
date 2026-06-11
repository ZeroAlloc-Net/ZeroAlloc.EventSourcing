using System.Text;
using FluentAssertions;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public sealed class GlobalStreamTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _adapter = new SqlServerEventStoreAdapter(_container.GetConnectionString());
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private static RawEvent MakeRaw(string eventType, string payload = "{}")
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Global_read_returns_events_from_all_streams_in_global_position_order()
    {
        await _adapter.AppendAsync(new StreamId("s1"), new[] { MakeRaw("X1") }.AsMemory(), StreamPosition.Start);
        await _adapter.AppendAsync(new StreamId("s2"), new[] { MakeRaw("Y1"), MakeRaw("Y2") }.AsMemory(), StreamPosition.Start);
        await _adapter.AppendAsync(new StreamId("s1"), new[] { MakeRaw("X2") }.AsMemory(), new StreamPosition(1));

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(4);
        read.Select(e => e.EventType).Should().Equal("X1", "Y1", "Y2", "X2");
        // Global positions are monotonic 1..4 — IDENTITY auto-assigns.
        read.Select(e => e.Position.Value).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Global_position_assigned_by_IDENTITY_is_monotonic()
    {
        await _adapter.AppendAsync(new StreamId("a"), new[] { MakeRaw("E1") }.AsMemory(), StreamPosition.Start);
        await _adapter.AppendAsync(new StreamId("b"), new[] { MakeRaw("E2") }.AsMemory(), StreamPosition.Start);
        await _adapter.AppendAsync(new StreamId("a"), new[] { MakeRaw("E3") }.AsMemory(), new StreamPosition(1));

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(3);
        read[0].EventType.Should().Be("E1");
        read[0].Position.Value.Should().Be(1);
        read[1].EventType.Should().Be("E2");
        read[1].Position.Value.Should().Be(2);
        read[2].EventType.Should().Be("E3");
        read[2].Position.Value.Should().Be(3);

        // Verify strictly increasing order
        for (var i = 1; i < read.Count; i++)
            read[i].Position.Value.Should().BeGreaterThan(read[i - 1].Position.Value);
    }

    [Fact]
    public async Task Per_stream_read_unaffected_by_global_counter()
    {
        // Interleave appends across two streams. The per-stream position must remain 1, 2, 3 for
        // each stream regardless of how the global counter advances.
        var s1 = new StreamId($"s1-{Guid.NewGuid()}");
        var s2 = new StreamId($"s2-{Guid.NewGuid()}");

        await _adapter.AppendAsync(s1, new[] { MakeRaw("A1") }.AsMemory(), StreamPosition.Start);
        await _adapter.AppendAsync(s2, new[] { MakeRaw("B1") }.AsMemory(), StreamPosition.Start);
        await _adapter.AppendAsync(s1, new[] { MakeRaw("A2") }.AsMemory(), new StreamPosition(1));
        await _adapter.AppendAsync(s2, new[] { MakeRaw("B2") }.AsMemory(), new StreamPosition(1));
        await _adapter.AppendAsync(s1, new[] { MakeRaw("A3") }.AsMemory(), new StreamPosition(2));

        var s1Events = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(s1, StreamPosition.Start))
            s1Events.Add(e);

        var s2Events = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(s2, StreamPosition.Start))
            s2Events.Add(e);

        s1Events.Should().HaveCount(3);
        s1Events.Select(e => e.Position.Value).Should().Equal(1, 2, 3);
        s1Events.Select(e => e.EventType).Should().Equal("A1", "A2", "A3");

        s2Events.Should().HaveCount(2);
        s2Events.Select(e => e.Position.Value).Should().Equal(1, 2);
        s2Events.Select(e => e.EventType).Should().Equal("B1", "B2");
    }
}
