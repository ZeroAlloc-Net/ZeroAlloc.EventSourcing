using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sqlite;

namespace ZeroAlloc.EventSourcing.Sqlite.Tests;

public sealed class SqliteEventStoreAdapterTests : IAsyncLifetime
{
    private SqliteConnection? _keepAlive;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        // Each test class gets its own in-memory DB. Keep-alive connection prevents the
        // shared-cache backing store from being reclaimed when transient connections close.
        _connectionString = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();
    }

    private SqliteEventStoreAdapter NewAdapter() => new SqliteEventStoreAdapter(_connectionString);

    private static RawEvent MakeRaw(string eventType, string payload = "{}")
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task EnsureSchemaAsync_creates_table_with_global_position_column()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('event_store') WHERE name = 'global_position'";
        var result = await cmd.ExecuteScalarAsync();
        result.Should().Be("global_position");
    }

    [Fact]
    public async Task EnsureSchemaAsync_is_idempotent()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();
        await adapter.EnsureSchemaAsync(); // must not throw

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('event_store') WHERE name = 'global_position'";
        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        count.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_to_empty_stream_succeeds_with_first_position()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        var result = await adapter.AppendAsync(
            new StreamId("order-1"),
            new[] { MakeRaw("OrderPlaced") }.AsMemory(),
            StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_with_wrong_expectedVersion_returns_Conflict()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();
        var id = new StreamId("order-conflict");

        await adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        // Second append with stale expectedVersion (still 0)
        var result = await adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task AppendAsync_with_empty_events_returns_success_without_changes()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        var result = await adapter.AppendAsync(
            new StreamId("order-empty"),
            ReadOnlyMemory<RawEvent>.Empty,
            StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(0);
    }

    [Fact]
    public async Task AppendAsync_assigns_monotonic_global_position_across_streams()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        await adapter.AppendAsync(new StreamId("a"), new[] { MakeRaw("E1") }.AsMemory(), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("b"), new[] { MakeRaw("E2") }.AsMemory(), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("a"), new[] { MakeRaw("E3") }.AsMemory(), new StreamPosition(1));

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(3);
        read[0].EventType.Should().Be("E1");
        read[0].Position.Value.Should().Be(1);
        read[1].EventType.Should().Be("E2");
        read[1].Position.Value.Should().Be(2);
        read[2].EventType.Should().Be("E3");
        read[2].Position.Value.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_per_stream_returns_events_in_position_order()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();
        var id = new StreamId("order-read");
        var events = new[] { MakeRaw("E1"), MakeRaw("E2"), MakeRaw("E3") }.AsMemory();
        await adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(3);
        read[0].Position.Value.Should().Be(1);
        read[1].Position.Value.Should().Be(2);
        read[2].Position.Value.Should().Be(3);
        read.Select(e => e.EventType).Should().Equal("E1", "E2", "E3");
    }

    [Fact]
    public async Task ReadAsync_per_stream_with_from_skips_earlier_events()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();
        var id = new StreamId("order-skip");
        var events = new[] { MakeRaw("E1"), MakeRaw("E2"), MakeRaw("E3") }.AsMemory();
        await adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, new StreamPosition(2)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("E2");
        read[1].EventType.Should().Be("E3");
    }

    [Fact]
    public async Task ReadAsync_global_returns_events_from_all_streams_in_global_position_order()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        await adapter.AppendAsync(new StreamId("s1"), new[] { MakeRaw("X1") }.AsMemory(), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s2"), new[] { MakeRaw("Y1"), MakeRaw("Y2") }.AsMemory(), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("s1"), new[] { MakeRaw("X2") }.AsMemory(), new StreamPosition(1));

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(4);
        read.Select(e => e.EventType).Should().Equal("X1", "Y1", "Y2", "X2");
        // Global positions are monotonic 1..4
        read.Select(e => e.Position.Value).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task ReadAsync_global_with_from_skips_earlier_global_positions()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        await adapter.AppendAsync(new StreamId("a"), new[] { MakeRaw("A1") }.AsMemory(), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("b"), new[] { MakeRaw("B1") }.AsMemory(), StreamPosition.Start);
        await adapter.AppendAsync(new StreamId("c"), new[] { MakeRaw("C1") }.AsMemory(), StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, new StreamPosition(2)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("B1");
        read[0].Position.Value.Should().Be(2);
        read[1].EventType.Should().Be("C1");
        read[1].Position.Value.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_unknown_stream_yields_nothing()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(new StreamId("nope"), StreamPosition.Start))
            read.Add(e);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscribeAsync_returns_a_subscription()
    {
        var adapter = NewAdapter();
        await adapter.EnsureSchemaAsync();

        var sub = await adapter.SubscribeAsync(
            new StreamId("s"),
            StreamPosition.Start,
            (_, _) => ValueTask.CompletedTask);

        sub.Should().NotBeNull();
        sub.Should().BeOfType<PollingEventSubscription>();
        await sub.DisposeAsync();
    }
}
