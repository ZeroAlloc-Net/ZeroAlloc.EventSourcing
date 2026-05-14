using Microsoft.Data.Sqlite;

namespace ZeroAlloc.EventSourcing.Benchmarks;

// Correctness-matched hand-rolled event store. Same SQLite connection as the ZA
// SqliteEventStoreAdapter in the benchmark. Same guarantees:
//   - Append checks current stream version inside a transaction (optimistic concurrency)
//   - Append INSERTs all events inside the same transaction
//   - Read returns events ordered by position
//
// Differences from the ZA path: no IEventStoreAdapter abstraction, no IEventStore
// wrapper, no IEventSerializer indirection, no IEventTypeRegistry. Caller hands
// the raw byte payload + event type tag straight in.
internal sealed class HandRolledSqliteEventStore
{
    private readonly SqliteConnection _conn;

    public HandRolledSqliteEventStore(SqliteConnection conn)
    {
        _conn = conn;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hand_events (
              stream_id TEXT NOT NULL,
              position INTEGER NOT NULL,
              event_type TEXT NOT NULL,
              payload BLOB NOT NULL,
              PRIMARY KEY (stream_id, position)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // Returns the new stream version on success, -1 on optimistic-concurrency conflict.
    public long Append(string streamId, long expectedVersion, string eventType, byte[] payload)
    {
        using var tx = _conn.BeginTransaction();
        using (var check = _conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT COALESCE(MAX(position), 0) FROM hand_events WHERE stream_id = $sid;";
            check.Parameters.AddWithValue("$sid", streamId);
            var current = (long)(check.ExecuteScalar() ?? 0L);
            if (current != expectedVersion)
                return -1;
        }
        var newVersion = expectedVersion + 1;
        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO hand_events (stream_id, position, event_type, payload) VALUES ($sid, $pos, $type, $payload);";
            ins.Parameters.AddWithValue("$sid", streamId);
            ins.Parameters.AddWithValue("$pos", newVersion);
            ins.Parameters.AddWithValue("$type", eventType);
            ins.Parameters.AddWithValue("$payload", payload);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return newVersion;
    }

    // Reads every event from the stream in position order, invoking the handler per event.
    // Matches the ZA read path: pull rows, hand each one back to the caller.
    public int ReadAll(string streamId, Action<long, string, byte[]> handler)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT position, event_type, payload FROM hand_events WHERE stream_id = $sid ORDER BY position;";
        cmd.Parameters.AddWithValue("$sid", streamId);
        using var r = cmd.ExecuteReader();
        var count = 0;
        while (r.Read())
        {
            handler(r.GetInt64(0), r.GetString(1), (byte[])r.GetValue(2));
            count++;
        }
        return count;
    }
}
