using System.Text;
using FluentAssertions;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

public sealed class SqlServerAdapterTests : IAsyncLifetime
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

    [Fact]
    public async Task SecondAppend_AfterSuccessfulFirst_Succeeds()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");

        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);
        var result = await _adapter.AppendAsync(id, new[] { MakeRaw("OrderShipped") }.AsMemory(), new StreamPosition(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(2);
    }

    [Fact]
    public async Task Read_PreservesMetadata()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var correlationId = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var metadata = new EventMetadata(eventId, "OrderPlaced", occurredAt, correlationId, causationId);
        var raw = new RawEvent(StreamPosition.Start, "OrderPlaced", "{}"u8.ToArray().AsMemory(), metadata);

        await _adapter.AppendAsync(id, new[] { raw }.AsMemory(), StreamPosition.Start);

        RawEvent read = default;
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read = e;

        read.Metadata.EventId.Should().Be(eventId);
        read.Metadata.OccurredAt.Should().Be(occurredAt);
        read.Metadata.CorrelationId.Should().Be(correlationId);
        read.Metadata.CausationId.Should().Be(causationId);
    }

    [Fact]
    public async Task Read_PreservesNullableMetadata_WhenNullCorrelationAndCausation()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var metadata = new EventMetadata(Guid.NewGuid(), "OrderPlaced", DateTimeOffset.UtcNow, null, null);
        var raw = new RawEvent(StreamPosition.Start, "OrderPlaced", "{}"u8.ToArray().AsMemory(), metadata);

        await _adapter.AppendAsync(id, new[] { raw }.AsMemory(), StreamPosition.Start);

        RawEvent read = default;
        await foreach (var e in _adapter.ReadAsync(id, StreamPosition.Start))
            read = e;

        read.Metadata.CorrelationId.Should().BeNull();
        read.Metadata.CausationId.Should().BeNull();
    }

    [Fact]
    public async Task EnsureSchemaAsync_CalledTwice_DoesNotThrow()
    {
        // IF NOT EXISTS guard must be idempotent
        var act = async () => await _adapter.EnsureSchemaAsync();
        await act.Should().NotThrowAsync();
    }
}
