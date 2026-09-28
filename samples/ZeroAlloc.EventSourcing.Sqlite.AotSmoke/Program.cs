// samples/ZeroAlloc.EventSourcing.Sqlite.AotSmoke/Program.cs
// Real SQLite adapter wiring under PublishAot=true. Validates:
//   - SqliteEventStoreAdapter constructs + opens connections AOT-clean
//   - EnsureSchemaAsync bootstraps the table
//   - AppendAsync writes an event through BEGIN IMMEDIATE
//   - ReadAsync round-trips it via per-stream cursor
//   - ReadAsync round-trips it via StreamId.Global (* global stream)
//   - EventStore over the adapter round-trips struct events whose nullable
//     value-type fields are set on one event and null on the other
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sqlite;
using ZeroAlloc.EventSourcing.Sqlite.AotSmoke;

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
    if (!e.Payload.Span.SequenceEqual(payload) || e.Position.Value != 1)
    {
        Console.Error.WriteLine(
            $"Sqlite AOT smoke FAIL: per-stream event expected payload 01-02-03 at 1, got "
            + $"{Convert.ToHexString(e.Payload.Span)} at {e.Position.Value}");
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

// Struct events with nullable value-type fields through the EventStore over SQLite.
var store = new EventStore(adapter, new ReadingSerializer(), new ReadingRegistry());
var readings = new object[]
{
    new MeterReading(1, 21.5m, DateTimeOffset.FromUnixTimeSeconds(1_780_000_000)),
    new MeterReading(2, null, null),
};
var readingsResult = await store.AppendAsync(
    new StreamId("meter-1"), readings.AsMemory(), StreamPosition.Start).ConfigureAwait(false);
if (!readingsResult.IsSuccess)
{
    Console.Error.WriteLine($"Sqlite AOT smoke FAIL: MeterReading append returned {readingsResult.Error.Code}");
    return 1;
}

var readBack = new List<MeterReading>();
await foreach (var envelope in store.ReadAsync(new StreamId("meter-1"), StreamPosition.Start).ConfigureAwait(false))
{
    if (envelope.Event is not MeterReading reading)
    {
        Console.Error.WriteLine($"Sqlite AOT smoke FAIL: expected a MeterReading, got {envelope.Event}");
        return 1;
    }
    readBack.Add(reading);
}

if (readBack.Count != 2 || readBack[0] != (MeterReading)readings[0] || readBack[1] != (MeterReading)readings[1])
{
    Console.Error.WriteLine(
        $"Sqlite AOT smoke FAIL: MeterReadings did not round-trip: {string.Join(", ", readBack)}");
    return 1;
}

Console.WriteLine("Sqlite AOT smoke PASS");
return 0;

namespace ZeroAlloc.EventSourcing.Sqlite.AotSmoke
{
    /// <summary>Struct event with nullable value-type fields.</summary>
    public readonly record struct MeterReading(long Sequence, decimal? Kwh, DateTimeOffset? TakenAt);

    /// <summary>Maps the one event name used here to its CLR type.</summary>
    internal sealed class ReadingRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventType, out Type? type)
        {
            type = string.Equals(eventType, nameof(MeterReading), StringComparison.Ordinal) ? typeof(MeterReading) : null;
            return type is not null;
        }

        public string GetTypeName(Type type) => type.Name;
    }

    /// <summary>
    /// Hand-rolled binary serializer for <see cref="MeterReading"/>, so the publish stays free of
    /// trim warnings. Layout: sequence, then a presence byte and value for each nullable field.
    /// </summary>
    internal sealed class ReadingSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        {
            if (@event is not MeterReading r)
                throw new NotSupportedException($"Unsupported event type {@event.GetType().FullName}");

            var buffer = new byte[8 + 1 + 16 + 1 + 10];
            var span = buffer.AsSpan();
            BinaryPrimitives.WriteInt64LittleEndian(span, r.Sequence);
            var offset = 8;
            span[offset++] = r.Kwh.HasValue ? (byte)1 : (byte)0;
            if (r.Kwh is { } kwh)
            {
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(kwh, bits);
                for (var i = 0; i < 4; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(span[(offset + (i * 4))..], bits[i]);
            }
            offset += 16;
            span[offset++] = r.TakenAt.HasValue ? (byte)1 : (byte)0;
            if (r.TakenAt is { } takenAt)
            {
                BinaryPrimitives.WriteInt64LittleEndian(span[offset..], takenAt.UtcTicks);
                BinaryPrimitives.WriteInt16LittleEndian(span[(offset + 8)..], (short)takenAt.Offset.TotalMinutes);
            }
            return buffer;
        }

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        {
            if (eventType != typeof(MeterReading))
                throw new NotSupportedException($"Unsupported event type {eventType.FullName}");

            var span = payload.Span;
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(span);
            var offset = 8;
            decimal? kwh = null;
            if (span[offset++] == 1)
            {
                Span<int> bits = stackalloc int[4];
                for (var i = 0; i < 4; i++)
                    bits[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + (i * 4))..]);
                kwh = new decimal(bits);
            }
            offset += 16;
            DateTimeOffset? takenAt = null;
            if (span[offset++] == 1)
            {
                var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
                var minutes = BinaryPrimitives.ReadInt16LittleEndian(span[(offset + 8)..]);
                var offsetSpan = TimeSpan.FromMinutes(minutes);
                takenAt = new DateTimeOffset(utcTicks + offsetSpan.Ticks, offsetSpan);
            }
            return new MeterReading(sequence, kwh, takenAt);
        }
    }
}
