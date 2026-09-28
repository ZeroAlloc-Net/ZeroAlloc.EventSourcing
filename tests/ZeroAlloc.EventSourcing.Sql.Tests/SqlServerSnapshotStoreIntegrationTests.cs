using System.Text.Json;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Integration tests for <see cref="SqlServerSnapshotStore{TState}"/> with Testcontainers SQL Server.
/// Tests against a real SQL Server database instance.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerSnapshotStoreIntegrationTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    /// <summary>Test aggregate state for integration testing.</summary>
    public struct OrderState
    {
        /// <summary>Order identifier.</summary>
        public string OrderId { get; set; }

        /// <summary>Order amount.</summary>
        public decimal Amount { get; set; }

        /// <summary>Order status.</summary>
        public string Status { get; set; }
    }

    private TestDatabase _database = null!;
    private SqlServerSnapshotStore<OrderState> _store = null!;
    private TestEventSerializer _serializer = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        _serializer = new TestEventSerializer();
        _store = new SqlServerSnapshotStore<OrderState>(_database.GetConnectionString(), _serializer);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task RoundTrip_WriteAndRead_WithRealDatabase()
    {
        var streamId = new StreamId("ss-test-order-1");
        var state = new OrderState { OrderId = "order-789", Amount = 750m, Status = "Confirmed" };
        var position = new StreamPosition(10);

        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.OrderId.Should().Be("order-789");
        result.Value.State.Amount.Should().Be(750m);
        result.Value.State.Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task MultipleWrites_LastWriteWins_WithRealDatabase()
    {
        var streamId = new StreamId("ss-test-upsert-1");

        var state1 = new OrderState { OrderId = "order-888", Amount = 200m, Status = "Pending" };
        await _store.WriteAsync(streamId, new StreamPosition(5), state1);

        var state2 = new OrderState { OrderId = "order-888", Amount = 350m, Status = "Shipped" };
        await _store.WriteAsync(streamId, new StreamPosition(15), state2);

        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(new StreamPosition(15));
        result.Value.State.Amount.Should().Be(350m);
        result.Value.State.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task LargePayload_Handles_WithRealDatabase()
    {
        var streamId = new StreamId("ss-test-large-1");
        var largeStatus = new string('Y', 5000);
        var state = new OrderState { OrderId = "large-order", Amount = 2000m, Status = largeStatus };

        await _store.WriteAsync(streamId, new StreamPosition(1), state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.State.Status.Length.Should().Be(5000);
        result.Value.State.Amount.Should().Be(2000m);
    }

    [Fact]
    public async Task MultipleStreams_Isolated_WithRealDatabase()
    {
        var stream1 = new StreamId("ss-test-s1");
        var stream2 = new StreamId("ss-test-s2");

        var state1 = new OrderState { OrderId = "s1-order", Amount = 333m };
        var state2 = new OrderState { OrderId = "s2-order", Amount = 444m };

        await _store.WriteAsync(stream1, new StreamPosition(1), state1);
        await _store.WriteAsync(stream2, new StreamPosition(1), state2);

        var result1 = await _store.ReadAsync(stream1);
        var result2 = await _store.ReadAsync(stream2);

        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Value.State.OrderId.Should().Be("s1-order");
        result1.Value.State.Amount.Should().Be(333m);
        result2.Value.State.OrderId.Should().Be("s2-order");
        result2.Value.State.Amount.Should().Be(444m);
    }

    /// <summary>A state type whose full name lies outside the database code page.</summary>
    public struct 注文State
    {
        /// <summary>Order amount.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Two stream ids that differ only outside the database code page keep a snapshot each.
    /// A VARCHAR key turned both into <c>??-consumer</c>, so the second write overwrote the first.
    /// </summary>
    [Fact]
    public async Task NonLatinStreamIds_KeepSeparateSnapshots()
    {
        await _store.WriteAsync(new StreamId(SqlServerSchemaInspector.JapanId), new StreamPosition(1),
            new OrderState { OrderId = "japan" });
        await _store.WriteAsync(new StreamId(SqlServerSchemaInspector.ChinaId), new StreamPosition(2),
            new OrderState { OrderId = "china" });

        var japan = await _store.ReadAsync(new StreamId(SqlServerSchemaInspector.JapanId));
        var china = await _store.ReadAsync(new StreamId(SqlServerSchemaInspector.ChinaId));

        japan.Should().NotBeNull();
        japan!.Value.State.OrderId.Should().Be("japan");
        china.Should().NotBeNull();
        china!.Value.State.OrderId.Should().Be("china");
    }

    /// <summary>
    /// The stored state type is compared with the type's full name on read, so a name outside the
    /// code page came back as <c>??State</c>, never matched, and the snapshot was never used.
    /// </summary>
    [Fact]
    public async Task NonLatinStateTypeName_RoundTrips()
    {
        var store = new SqlServerSnapshotStore<注文State>(_database.GetConnectionString(), _serializer);
        var streamId = new StreamId("ss-non-latin-state");

        await store.WriteAsync(streamId, new StreamPosition(3), new 注文State { Amount = 12m });
        var result = await store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result!.Value.State.Amount.Should().Be(12m);
    }

    /// <summary>
    /// A table created with the VARCHAR columns of earlier versions is converted to NVARCHAR in
    /// place, keeping its rows and its primary key, and running the migration again changes nothing.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_MigratesLegacyVarcharTable()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);
        await _store.WriteAsync(new StreamId("legacy"), new StreamPosition(4), new OrderState { OrderId = "old" });

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        var types = await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "snapshots");
        types["stream_id"].Should().Be("nvarchar(255)");
        types["state_type"].Should().Be("nvarchar(500)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "snapshots"))
            .Should().Equal("PK CLUSTERED (stream_id)");
        var legacy = await _store.ReadAsync(new StreamId("legacy"));
        legacy.Should().NotBeNull();
        legacy!.Value.State.OrderId.Should().Be("old");

        await NonLatinStreamIds_KeepSeparateSnapshots();
        await NonLatinStateTypeName_RoundTrips();
    }

    /// <summary>App instances that start together run the migration once between them.</summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConcurrentCallsMigrateLegacyTableOnce()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new SqlServerSnapshotStore<OrderState>(connectionString, _serializer).EnsureSchemaAsync().AsTask()));

        var types = await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "snapshots");
        types["stream_id"].Should().Be("nvarchar(255)");
        types["state_type"].Should().Be("nvarchar(500)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "snapshots"))
            .Should().Equal("PK CLUSTERED (stream_id)");
    }

    // The table as versions before #384 created it.
    private static Task CreateLegacyTableAsync(string connectionString) =>
        SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            DROP TABLE dbo.snapshots;
            CREATE TABLE dbo.snapshots (
                stream_id   VARCHAR(255)       NOT NULL,
                position    BIGINT             NOT NULL,
                state_type  VARCHAR(500)       NOT NULL,
                payload     VARBINARY(MAX)     NOT NULL,
                created_at  DATETIMEOFFSET     NOT NULL,
                CONSTRAINT PK_snapshots PRIMARY KEY (stream_id)
            );
            """);

    /// <summary>
    /// Simple serializer for test purposes using JSON.
    /// </summary>
    private sealed class TestEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        {
            var json = JsonSerializer.Serialize(@event);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
        {
            var json = System.Text.Encoding.UTF8.GetString(data.Span);
            return JsonSerializer.Deserialize(json, targetType) ?? throw new InvalidOperationException("Deserialization returned null");
        }
    }
}
