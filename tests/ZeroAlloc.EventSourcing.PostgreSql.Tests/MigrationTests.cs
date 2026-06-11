using System.Text;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.PostgreSql;

namespace ZeroAlloc.EventSourcing.PostgreSql.Tests;

public sealed class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task DropTableAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS event_store;
            DROP SEQUENCE IF EXISTS event_store_global_position_seq;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ColumnExistsAsync(string column)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM information_schema.columns
            WHERE table_name = 'event_store' AND column_name = @col
            """;
        cmd.Parameters.AddWithValue("col", column);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<int> CountColumnsAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'event_store'";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task EnsureSchema_on_fresh_database_creates_table_with_global_position()
    {
        await DropTableAsync();

        var adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await adapter.EnsureSchemaAsync();

        (await ColumnExistsAsync("global_position")).Should().BeTrue();
        (await ColumnExistsAsync("stream_id")).Should().BeTrue();
        (await ColumnExistsAsync("position")).Should().BeTrue();
        (await ColumnExistsAsync("event_type")).Should().BeTrue();
        (await ColumnExistsAsync("payload")).Should().BeTrue();

        // BIGSERIAL must auto-assign on INSERT (verified end-to-end via AppendAsync).
        var id = new StreamId($"orders-{Guid.NewGuid()}");
        var payload = Encoding.UTF8.GetBytes("{}");
        var raw = new RawEvent(StreamPosition.Start, "Created", payload.AsMemory(), EventMetadata.New("Created"));
        var result = await adapter.AppendAsync(id, new[] { raw }.AsMemory(), StreamPosition.Start);
        result.IsSuccess.Should().BeTrue();

        var read = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start))
            read.Add(e);

        read.Should().HaveCount(1);
        read[0].Position.Value.Should().Be(1);
    }

    [Fact]
    public async Task EnsureSchema_on_legacy_table_without_global_position_migrates_in_place()
    {
        await DropTableAsync();

        // Create legacy schema (no global_position column / no sequence / no index).
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE event_store (
                    stream_id      TEXT         NOT NULL,
                    position       BIGINT       NOT NULL,
                    event_type     TEXT         NOT NULL,
                    event_id       UUID         NOT NULL,
                    occurred_at    TIMESTAMPTZ  NOT NULL,
                    correlation_id UUID         NULL,
                    causation_id   UUID         NULL,
                    payload        BYTEA        NOT NULL,
                    PRIMARY KEY (stream_id, position)
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Pre-seed 5 events across 2 streams, with deliberately interleaved occurred_at timestamps
        // so the ROW_NUMBER backfill ordering (occurred_at, stream_id, position) is observable.
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedLegacyEventAsync("alpha", 1, "A1", baseTime.AddSeconds(0));
        await SeedLegacyEventAsync("beta",  1, "B1", baseTime.AddSeconds(1));
        await SeedLegacyEventAsync("alpha", 2, "A2", baseTime.AddSeconds(2));
        await SeedLegacyEventAsync("beta",  2, "B2", baseTime.AddSeconds(3));
        await SeedLegacyEventAsync("alpha", 3, "A3", baseTime.AddSeconds(4));

        // Run the migration.
        var adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await adapter.EnsureSchemaAsync();

        (await ColumnExistsAsync("global_position")).Should().BeTrue();

        // Verify backfill order: (occurred_at ASC, stream_id ASC, position ASC).
        // Expected: A1(1), B1(2), A2(3), B2(4), A3(5)
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT event_type, global_position FROM event_store ORDER BY global_position ASC";
            await using var reader = await cmd.ExecuteReaderAsync();
            var pairs = new List<(string, long)>();
            while (await reader.ReadAsync())
                pairs.Add((reader.GetString(0), reader.GetInt64(1)));

            pairs.Should().Equal(
                ("A1", 1L),
                ("B1", 2L),
                ("A2", 3L),
                ("B2", 4L),
                ("A3", 5L));
        }

        // Verify SEQUENCE is wired: a fresh AppendAsync auto-assigns global_position = 6.
        var newEvent = new RawEvent(
            StreamPosition.Start,
            "Gamma",
            Encoding.UTF8.GetBytes("{}").AsMemory(),
            EventMetadata.New("Gamma"));
        var result = await adapter.AppendAsync(new StreamId("gamma"), new[] { newEvent }.AsMemory(), StreamPosition.Start);
        result.IsSuccess.Should().BeTrue();

        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT global_position FROM event_store WHERE stream_id = 'gamma'";
            var gp = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            gp.Should().Be(6L);
        }
    }

    [Fact]
    public async Task EnsureSchema_is_idempotent_on_already_upgraded_table()
    {
        await DropTableAsync();

        var adapter = new PostgreSqlEventStoreAdapter(_dataSource);
        await adapter.EnsureSchemaAsync();
        var firstColumnCount = await CountColumnsAsync();

        // Second invocation must be a no-op.
        await adapter.EnsureSchemaAsync();
        var secondColumnCount = await CountColumnsAsync();

        secondColumnCount.Should().Be(firstColumnCount);
        (await ColumnExistsAsync("global_position")).Should().BeTrue();

        // Third invocation — still a no-op, still functional.
        await adapter.EnsureSchemaAsync();
        var id = new StreamId($"idem-{Guid.NewGuid()}");
        var raw = new RawEvent(StreamPosition.Start, "E", "{}"u8.ToArray().AsMemory(), EventMetadata.New("E"));
        var result = await adapter.AppendAsync(id, new[] { raw }.AsMemory(), StreamPosition.Start);
        result.IsSuccess.Should().BeTrue();
    }

    private async Task SeedLegacyEventAsync(string streamId, long position, string eventType, DateTimeOffset occurredAt)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_store (stream_id, position, event_type, event_id, occurred_at, correlation_id, causation_id, payload)
            VALUES (@streamId, @position, @eventType, @eventId, @occurredAt, NULL, NULL, @payload)
            """;
        cmd.Parameters.AddWithValue("streamId", streamId);
        cmd.Parameters.AddWithValue("position", position);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("eventId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("occurredAt", occurredAt.UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea) { Value = Encoding.UTF8.GetBytes("{}") });
        await cmd.ExecuteNonQueryAsync();
    }
}
