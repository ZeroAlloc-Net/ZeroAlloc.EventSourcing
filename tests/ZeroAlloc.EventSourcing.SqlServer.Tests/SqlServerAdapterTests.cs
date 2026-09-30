using System.Text;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.SqlServer;

namespace ZeroAlloc.EventSourcing.SqlServer.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerAdapterTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerEventStoreAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _adapter = new SqlServerEventStoreAdapter(_database.GetConnectionString());
        await _adapter.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
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
        // EXCLUSIVE semantics: from=2 returns events with position > 2 — only OrderDelivered (pos=3).
        await foreach (var e in _adapter.ReadAsync(id, new StreamPosition(2)))
            read.Add(e);

        read.Should().HaveCount(1);
        read[0].EventType.Should().Be("OrderDelivered");
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

    // Issue 421: a read cancelled while its command runs on the server surfaces as an
    // OperationCanceledException. When the cancellation races the server's reply, SqlClient throws
    // a SqlException instead, which the adapter turns into an OperationCanceledException; that race
    // cannot be forced from a test. The read is held behind an exclusive table lock, so it is
    // certainly in flight when the token is cancelled.
    [Fact]
    public async Task ReadAsync_CancelledWhileCommandInFlight_ThrowsOperationCanceledException()
    {
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        await _adapter.AppendAsync(id, new[] { MakeRaw("OrderPlaced") }.AsMemory(), StreamPosition.Start);

        var connectionString = _database.GetConnectionString();
        await using var locker = new SqlConnection(connectionString);
        await locker.OpenAsync();
        await using var lockTx = (SqlTransaction)await locker.BeginTransactionAsync();
        await using var lockCmd = locker.CreateCommand();
        lockCmd.Transaction = lockTx;
        lockCmd.CommandText = "SELECT TOP 0 1 FROM dbo.event_store WITH (TABLOCKX, HOLDLOCK); SELECT @@SPID;";
        var lockerSpid = Convert.ToInt32(await lockCmd.ExecuteScalarAsync());

        using var cts = new CancellationTokenSource();
        var read = Task.Run(async () =>
        {
            await foreach (var _ in _adapter.ReadAsync(id, StreamPosition.Start, cts.Token)) { }
        });

        await WaitUntilBlockedByAsync(connectionString, lockerSpid, read);
        await cts.CancelAsync();

        var act = async () => await read;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task WaitUntilBlockedByAsync(string connectionString, int blockingSpid, Task read)
    {
        await using var monitor = new SqlConnection(connectionString);
        await monitor.OpenAsync();
        await using var cmd = monitor.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @spid";
        cmd.Parameters.AddWithValue("@spid", blockingSpid);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((int)(await cmd.ExecuteScalarAsync())! == 0)
        {
            if (read.IsCompleted) await read; // surfaces an unexpected failure
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The read never blocked on the table lock.");
            await Task.Delay(20);
        }
    }
}
