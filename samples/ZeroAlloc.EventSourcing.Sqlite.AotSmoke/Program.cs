// samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke/Program.cs
// Real SQLite adapter wiring under PublishAot=true. Validates:
//   - SqliteEventStoreAdapter constructs + opens connections AOT-clean
//   - EnsureSchemaAsync bootstraps the table
//   - AppendAsync writes an event through BEGIN IMMEDIATE
//   - ReadAsync round-trips it via per-stream cursor
//   - ReadAsync round-trips it via StreamId.Global (* global stream)
using Microsoft.Data.Sqlite;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sqlite;

var connectionString = $"Data Source=file:aot-smoke-{Guid.NewGuid():N}?mode=memory&cache=shared";

// Keep-alive connection prevents the shared-cache backing store from being
// reclaimed when transient connections close inside the adapter.
using var keepAlive = new SqliteConnection(connectionString);
keepAlive.Open();

var adapter = new SqliteEventStoreAdapter(connectionString);
await adapter.EnsureSchemaAsync().ConfigureAwait(false);

var payload = new byte[] { 0x01, 0x02, 0x03 };
var metadata = EventMetadata.New("SmokeEvent");
var raw = new RawEvent(StreamPosition.Start, "SmokeEvent", payload.AsMemory(), metadata);

var appendResult = await adapter.AppendAsync(
    new StreamId("smoke-1"),
    new[] { raw }.AsMemory(),
    StreamPosition.Start).ConfigureAwait(false);

if (!appendResult.IsSuccess)
{
    Console.Error.WriteLine($"Sqlite AOT smoke FAIL: append returned {appendResult.Error.Code}");
    return 1;
}

// Per-stream read
var perStreamCount = 0;
await foreach (var e in adapter.ReadAsync(new StreamId("smoke-1"), StreamPosition.Start).ConfigureAwait(false))
{
    perStreamCount++;
    if (!string.Equals(e.EventType, "SmokeEvent", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Sqlite AOT smoke FAIL: per-stream EventType expected 'SmokeEvent', got '{e.EventType}'");
        return 1;
    }
}

if (perStreamCount != 1)
{
    Console.Error.WriteLine($"Sqlite AOT smoke FAIL: expected 1 event via per-stream, got {perStreamCount}");
    return 1;
}

// Global stream read
var globalCount = 0;
await foreach (var e in adapter.ReadAsync(StreamId.Global, StreamPosition.Start).ConfigureAwait(false))
{
    globalCount++;
    if (!string.Equals(e.EventType, "SmokeEvent", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Sqlite AOT smoke FAIL: global EventType expected 'SmokeEvent', got '{e.EventType}'");
        return 1;
    }
}

if (globalCount != 1)
{
    Console.Error.WriteLine($"Sqlite AOT smoke FAIL: expected 1 event via global, got {globalCount}");
    return 1;
}

Console.WriteLine("Sqlite AOT smoke PASS");
return 0;
