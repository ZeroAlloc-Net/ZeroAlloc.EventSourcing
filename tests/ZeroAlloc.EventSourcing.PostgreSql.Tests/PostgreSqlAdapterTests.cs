using System.Text;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

public sealed class PostgreSqlAdapterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();
    private PostgreSqlEventStoreAdapter _adapter = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static RawEvent MakeRaw(string eventType, string payload = "{}")
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return new RawEvent(StreamPosition.Start, eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    [Fact]
    public async Task Append_ToNewStream_Succeeds_AndReturnsPosition1()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[] { MakeRaw("OrderPlaced") }.AsMemory();

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    [Fact]
    public async Task Append_WithWrongExpectedVersion_ReturnsConflict()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[] { MakeRaw("OrderPlaced") }.AsMemory();

        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        // Second append still uses expectedVersion=0 (stale)
        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Append_MultipleEvents_AssignsConsecutivePositions()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
        }.AsMemory();

        var result = await _adapter.AppendAsync(id, events, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_AfterAppend_ReturnsEventsInOrder()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
        }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderPlaced");
        read[0].Position.Value.Should().Be(1);
        read[1].EventType.Should().Be("OrderShipped");
        read[1].Position.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_FromPosition_ReturnsEventsFromThatPositionOnward()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var events = new[]
        {
            MakeRaw("OrderPlaced"),
            MakeRaw("OrderShipped"),
            MakeRaw("OrderDelivered"),
        }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, new StreamPosition(2)))
            read.Add(e);

        read.Should().HaveCount(2);
        read[0].EventType.Should().Be("OrderShipped");
        read[1].EventType.Should().Be("OrderDelivered");
    }

    [Fact]
    public async Task Read_NonExistentStream_ReturnsEmpty()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var read = new List<RawEvent>();
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read.Add(e);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_PreservesPayload()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var payload = """{"orderId":"abc","amount":42}""";
        var events = new[] { MakeRaw("OrderPlaced", payload) }.AsMemory();
        await _adapter.AppendAsync(id, events, StreamPosition.Start);

        RawEvent read = default;
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read = e;

        Encoding.UTF8.GetString(read.Payload.Span).Should().Be(payload);
    }

    [Fact]
    public async Task Append_EmptyEventList_Succeeds_WithUnchangedVersion()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        var result = await _adapter.AppendAsync(id, ReadOnlyMemory<RawEvent>.Empty, StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(0);
    }
}
