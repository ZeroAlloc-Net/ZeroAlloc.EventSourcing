using Microsoft.Data.Sqlite;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Benchmarks;

// IEventStoreAdapter that talks directly to SQLite via Microsoft.Data.Sqlite.
// Used only by HandRolledOverheadBenchmark — the ZA row runs against THIS adapter
// while the HandRolledSqliteEventStore row uses a parallel SQLite table via raw SQL
// against the SAME connection. Storage variance cancels out of the delta, so the
// remaining number is the ZA abstraction cost on top of the same INSERT/SELECT.
internal sealed class SqliteEventStoreAdapter : IEventStoreAdapter
{
    private readonly SqliteConnection _conn;

    public SqliteEventStoreAdapter(SqliteConnection conn)
    {
        _conn = conn;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS za_events (
              stream_id TEXT NOT NULL,
              position INTEGER NOT NULL,
              event_type TEXT NOT NULL,
              payload BLOB NOT NULL,
              event_id BLOB NOT NULL,
              occurred_at TEXT NOT NULL,
              PRIMARY KEY (stream_id, position)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        using var tx = _conn.BeginTransaction();
        using var check = _conn.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT COALESCE(MAX(position), 0) FROM za_events WHERE stream_id = $sid;";
        check.Parameters.AddWithValue("$sid", id.Value);
        var current = (long)(check.ExecuteScalar() ?? 0L);

        if (current != expectedVersion.Value)
        {
            return ValueTask.FromResult(Result<AppendResult, StoreError>.Failure(
                StoreError.Conflict(id, expectedVersion, new StreamPosition(current))));
        }

        var span = events.Span;
        for (var i = 0; i < span.Length; i++)
        {
            var e = span[i];
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO za_events (stream_id, position, event_type, payload, event_id, occurred_at)
                VALUES ($sid, $pos, $type, $payload, $eid, $occurred);
                """;
            cmd.Parameters.AddWithValue("$sid", id.Value);
            cmd.Parameters.AddWithValue("$pos", e.Position.Value);
            cmd.Parameters.AddWithValue("$type", e.EventType);
            cmd.Parameters.AddWithValue("$payload", e.Payload.ToArray());
            cmd.Parameters.AddWithValue("$eid", e.Metadata.EventId.ToByteArray());
            cmd.Parameters.AddWithValue("$occurred", e.Metadata.OccurredAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        return ValueTask.FromResult(Result<AppendResult, StoreError>.Success(
            new AppendResult(id, new StreamPosition(expectedVersion.Value + span.Length))));
    }

    public async IAsyncEnumerable<RawEvent> ReadAsync(
        StreamId id,
        StreamPosition from,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT position, event_type, payload, event_id, occurred_at
            FROM za_events
            WHERE stream_id = $sid AND position > $from
            ORDER BY position;
            """;
        cmd.Parameters.AddWithValue("$sid", id.Value);
        cmd.Parameters.AddWithValue("$from", from.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var pos = r.GetInt64(0);
            var type = r.GetString(1);
            var payload = (byte[])r.GetValue(2);
            var eventIdBytes = (byte[])r.GetValue(3);
            var occurred = DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture);
            yield return new RawEvent(
                new StreamPosition(pos),
                type,
                payload,
                new EventMetadata(new Guid(eventIdBytes), type, occurred, null, null));
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => throw new NotSupportedException("Subscriptions not exercised by this benchmark.");
}
