using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerDeadLetterStoreTests(SqlServerContainerFixture fixture) : DeadLetterStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerDeadLetterStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync().ConfigureAwait(false);
        _store = new SqlServerDeadLetterStore(_database.GetConnectionString(), new JsonEventSerializer());
        await _store.EnsureSchemaAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
    }

    protected override IDeadLetterStore CreateStore() => _store;

    [Fact]
    public async Task EnsureSchemaAsync_UpgradesTableFromBeforeMetadataColumns()
    {
        var failedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await using (var conn = new SqlConnection(_database.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // The dead_letters schema as the store created it before it kept event metadata.
            cmd.CommandText = """
                DROP TABLE dbo.dead_letters;
                CREATE TABLE dbo.dead_letters (
                    id                BIGINT IDENTITY(1,1) PRIMARY KEY,
                    consumer_id       VARCHAR(256)         NOT NULL,
                    stream_id         VARCHAR(255)         NOT NULL,
                    position          BIGINT               NOT NULL,
                    event_type        VARCHAR(500)         NOT NULL,
                    payload           VARBINARY(MAX)       NOT NULL,
                    exception_type    VARCHAR(500)         NOT NULL,
                    exception_message NVARCHAR(MAX)        NOT NULL,
                    failed_at         DATETIMEOFFSET       NOT NULL
                );
                INSERT INTO dbo.dead_letters
                    (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)
                VALUES ('legacy', 's', 1, 'Old', 0x7B7D, 'Exception', 'old', @failed_at);
                """;
            cmd.Parameters.AddWithValue("@failed_at", failedAt);
            await cmd.ExecuteNonQueryAsync();
        }

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        var metadata = new EventMetadata(Guid.NewGuid(), "New", failedAt, Guid.NewGuid(), null);
        var envelope = new EventEnvelope(new StreamId("s"), new StreamPosition(2), new object(), metadata);
        await _store.WriteAsync("current", envelope, new InvalidOperationException("new"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in _store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(2);
        results[0].Envelope.Metadata.EventId.Should().Be(Guid.Empty, "the row predates the event_id column");
        results[0].Envelope.Metadata.OccurredAt.Should().Be(failedAt);
        results[1].Envelope.Metadata.Should().Be(metadata);
    }

    /// <summary>
    /// Every string the store keeps comes back as written when it lies outside the database code
    /// page. VARCHAR columns turned each such character into <c>?</c>.
    /// </summary>
    [Fact]
    public async Task NonLatinStrings_RoundTrip()
    {
        var metadata = new EventMetadata(Guid.NewGuid(), "注文Created", DateTimeOffset.UtcNow, null, null);
        var envelope = new EventEnvelope(new StreamId(SqlServerSchemaInspector.ChinaId), new StreamPosition(1), new object(), metadata);

        await _store.WriteAsync(SqlServerSchemaInspector.JapanId, envelope, new 例外Exception());

        var results = new List<DeadLetterEntry>();
        await foreach (var e in _store.ReadAllAsync())
            results.Add(e);

        var entry = results.Should().ContainSingle().Subject;
        entry.ConsumerId.Should().Be(SqlServerSchemaInspector.JapanId);
        entry.Envelope.StreamId.Value.Should().Be(SqlServerSchemaInspector.ChinaId);
        entry.Envelope.Metadata.EventType.Should().Be("注文Created");
        entry.ExceptionType.Should().Be(nameof(例外Exception));
    }

    /// <summary>
    /// A table created with the VARCHAR columns of earlier versions is converted to NVARCHAR in
    /// place. Its rows stay, and so do its primary key and an index an operator added on a
    /// converted column. Running the migration again changes nothing.
    /// </summary>
    [Fact]
    public async Task EnsureSchemaAsync_MigratesLegacyVarcharTable()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        await AssertMigratedAsync(connectionString);
        await NonLatinStrings_RoundTripAfterLegacyRowAsync();
    }

    /// <summary>App instances that start together run the migration once between them.</summary>
    [Fact]
    public async Task EnsureSchemaAsync_ConcurrentCallsMigrateLegacyTableOnce()
    {
        var connectionString = _database.GetConnectionString();
        await CreateLegacyTableAsync(connectionString);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new SqlServerDeadLetterStore(connectionString, new JsonEventSerializer()).EnsureSchemaAsync().AsTask()));

        await AssertMigratedAsync(connectionString);
    }

    private static async Task AssertMigratedAsync(string connectionString)
    {
        var types = await SqlServerSchemaInspector.GetColumnTypesAsync(connectionString, "dead_letters");
        types["consumer_id"].Should().Be("nvarchar(256)");
        types["stream_id"].Should().Be("nvarchar(255)");
        types["event_type"].Should().Be("nvarchar(500)");
        types["exception_type"].Should().Be("nvarchar(500)");
        (await SqlServerSchemaInspector.GetIndexesAsync(connectionString, "dead_letters")).Should().BeEquivalentTo(
            "PK CLUSTERED (id)",
            "INDEX NONCLUSTERED (consumer_id, failed_at DESC) INCLUDE (stream_id)");
        (await SqlServerSchemaInspector.CountRowsAsync(connectionString, "dead_letters")).Should().Be(1);
    }

    private async Task NonLatinStrings_RoundTripAfterLegacyRowAsync()
    {
        var metadata = new EventMetadata(Guid.NewGuid(), "注文Created", DateTimeOffset.UtcNow, null, null);
        var envelope = new EventEnvelope(new StreamId(SqlServerSchemaInspector.ChinaId), new StreamPosition(1), new object(), metadata);
        await _store.WriteAsync(SqlServerSchemaInspector.JapanId, envelope, new 例外Exception());

        var results = new List<DeadLetterEntry>();
        await foreach (var e in _store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(2);
        results[0].ConsumerId.Should().Be("legacy");
        results[1].ConsumerId.Should().Be(SqlServerSchemaInspector.JapanId);
        results[1].Envelope.StreamId.Value.Should().Be(SqlServerSchemaInspector.ChinaId);
        results[1].Envelope.Metadata.EventType.Should().Be("注文Created");
        results[1].ExceptionType.Should().Be(nameof(例外Exception));
    }

    // The table as versions before #384 created it, with an index an operator might have added.
    private static Task CreateLegacyTableAsync(string connectionString) =>
        SqlServerSchemaInspector.ExecuteAsync(connectionString, """
            DROP TABLE dbo.dead_letters;
            CREATE TABLE dbo.dead_letters (
                id                BIGINT IDENTITY(1,1) PRIMARY KEY,
                consumer_id       VARCHAR(256)         NOT NULL,
                stream_id         VARCHAR(255)         NOT NULL,
                position          BIGINT               NOT NULL,
                event_type        VARCHAR(500)         NOT NULL,
                payload           VARBINARY(MAX)       NOT NULL,
                exception_type    VARCHAR(500)         NOT NULL,
                exception_message NVARCHAR(MAX)        NOT NULL,
                failed_at         DATETIMEOFFSET       NOT NULL,
                event_id          UNIQUEIDENTIFIER     NULL,
                occurred_at       DATETIMEOFFSET       NULL,
                correlation_id    UNIQUEIDENTIFIER     NULL,
                causation_id      UNIQUEIDENTIFIER     NULL
            );
            CREATE INDEX IX_dead_letters_consumer
                ON dbo.dead_letters (consumer_id, failed_at DESC) INCLUDE (stream_id);
            INSERT INTO dbo.dead_letters
                (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)
            VALUES ('legacy', 's', 1, 'Old', 0x7B7D, 'Exception', 'old', SYSDATETIMEOFFSET());
            """);

    private sealed class 例外Exception() : Exception("non-Latin exception type");

    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event));

        public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
            => JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(data.Span), targetType)
               ?? throw new InvalidOperationException("Deserialization returned null");
    }
}
