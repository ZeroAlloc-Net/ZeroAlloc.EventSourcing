using System.Text.Json;
using AwesomeAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlDeadLetterStoreTests(PostgreSqlContainerFixture fixture) : SerializingDeadLetterStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlDeadLetterStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync().ConfigureAwait(false);
        _dataSource = NpgsqlDataSource.Create(_database.GetConnectionString());
        _store = new PostgreSqlDeadLetterStore(_dataSource, new JsonEventSerializer(), new DeadLetterTestEventTypeRegistry());
        await _store.EnsureSchemaAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        await _database.DisposeAsync().ConfigureAwait(false);
    }

    protected override IDeadLetterStore CreateStore() => _store;

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var act = () => new PostgreSqlDeadLetterStore(_dataSource, new JsonEventSerializer(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("registry");
    }

    // The obsolete constructor keeps its behaviour until the next major removes it: without a
    // registry it cannot resolve the event type, so it reads back the stored payload bytes.
    [Fact]
    public async Task ObsoleteConstructor_ReadsBackThePayloadBytes()
    {
#pragma warning disable ZAES004
        var store = new PostgreSqlDeadLetterStore(_dataSource, new JsonEventSerializer());
#pragma warning restore ZAES004
        var envelope = new EventEnvelope(
            new StreamId("s"), new StreamPosition(1), new DeadLetterTestEvent("o", 1),
            new EventMetadata(Guid.NewGuid(), "UnregisteredEvent", DateTimeOffset.UtcNow, null, null));
        await store.WriteAsync("consumer-1", envelope, new InvalidOperationException("boom"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        var payload = results.Should().ContainSingle().Which.Envelope.Event.Should().BeOfType<byte[]>().Subject;
        JsonSerializer.Deserialize<DeadLetterTestEvent>(payload).Should().Be(new DeadLetterTestEvent("o", 1));
    }

    [Fact]
    public async Task EnsureSchemaAsync_UpgradesTableFromBeforeMetadataColumns()
    {
        var failedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            // The dead_letters schema as the store created it before it kept event metadata.
            cmd.CommandText = """
                DROP TABLE dead_letters;
                CREATE TABLE dead_letters (
                    id               BIGSERIAL       PRIMARY KEY,
                    consumer_id      VARCHAR(256)    NOT NULL,
                    stream_id        VARCHAR(255)    NOT NULL,
                    position         BIGINT          NOT NULL,
                    event_type       VARCHAR(500)    NOT NULL,
                    payload          BYTEA           NOT NULL,
                    exception_type   VARCHAR(500)    NOT NULL,
                    exception_message TEXT           NOT NULL,
                    failed_at        TIMESTAMPTZ     NOT NULL
                );
                INSERT INTO dead_letters
                    (consumer_id, stream_id, position, event_type, payload, exception_type, exception_message, failed_at)
                VALUES ('legacy', 's', 1, 'DeadLetterTestEvent',
                        convert_to('{"OrderId":"legacy","Quantity":1}', 'UTF8'), 'Exception', 'old', @failed_at);
                """;
            cmd.Parameters.AddWithValue("@failed_at", failedAt);
            await cmd.ExecuteNonQueryAsync();
        }

        await _store.EnsureSchemaAsync();
        await _store.EnsureSchemaAsync();

        var metadata = new EventMetadata(Guid.NewGuid(), DeadLetterTestEvent.TypeName, failedAt, Guid.NewGuid(), null);
        var envelope = new EventEnvelope(new StreamId("s"), new StreamPosition(2), new DeadLetterTestEvent("new", 2), metadata);
        await _store.WriteAsync("current", envelope, new InvalidOperationException("new"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in _store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(2);
        results[0].Envelope.Metadata.EventId.Should().Be(Guid.Empty, "the row predates the event_id column");
        results[0].Envelope.Metadata.OccurredAt.Should().Be(failedAt);
        results[0].Envelope.Event.Should().Be(new DeadLetterTestEvent("legacy", 1));
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
