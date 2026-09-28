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

    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event));

        public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
            => JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(data.Span), targetType)
               ?? throw new InvalidOperationException("Deserialization returned null");
    }
}
