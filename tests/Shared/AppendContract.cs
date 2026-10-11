using AwesomeAssertions;

namespace ZeroAlloc.EventSourcing.Testing;

/// <summary>
/// The append contract of a SQL <see cref="IEventStoreAdapter"/>, run against each adapter. A
/// multi-event append writes every event with its own values through one reused command, and is
/// atomic: when one insert fails, none of the batch is stored. See #472.
/// </summary>
internal static class AppendContract
{
    public static async Task A_multi_event_append_stores_every_field_of_every_event(IEventStoreAdapter adapter)
    {
        var id = new StreamId($"append-{Guid.NewGuid():N}");
        var correlation = Guid.NewGuid();
        var causation = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

        // Payloads the serializer can hand over: a whole array, a slice in the middle of a larger
        // buffer, an empty slice of a non-empty buffer, and a slice too large for a short binary.
        var buffer = new byte[20_000];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)(i % 251);
        var whole = new byte[] { 1, 2, 3 };
        var middle = new ReadOnlyMemory<byte>(buffer, 7, 5);
        var empty = new ReadOnlyMemory<byte>(buffer, 3, 0);
        var large = new ReadOnlyMemory<byte>(buffer, 11, 10_000);

        var events = new[]
        {
            Raw("Whole", whole, new EventMetadata(Guid.NewGuid(), "Whole", at, correlation, causation)),
            Raw("Middle", middle, new EventMetadata(Guid.NewGuid(), "Middle", at.AddSeconds(1), null, null)),
            Raw("Empty", empty, new EventMetadata(Guid.NewGuid(), "Empty", at.AddSeconds(2), correlation, null)),
            Raw("Large", large, new EventMetadata(Guid.NewGuid(), "Large", at.AddSeconds(3), null, causation)),
        };

        var result = await adapter.AppendAsync(id, events.AsMemory(), StreamPosition.Start);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextExpectedVersion.Value.Should().Be(4);

        var read = await ReadAllAsync(adapter, id);
        read.Should().HaveCount(4);
        for (var i = 0; i < events.Length; i++)
        {
            read[i].Position.Value.Should().Be(i + 1);
            read[i].EventType.Should().Be(events[i].EventType);
            read[i].Payload.ToArray().Should().Equal(events[i].Payload.ToArray(), $"the payload of {events[i].EventType} round-trips");
            read[i].Metadata.EventId.Should().Be(events[i].Metadata.EventId);
            read[i].Metadata.OccurredAt.Should().Be(events[i].Metadata.OccurredAt);
            read[i].Metadata.CorrelationId.Should().Be(events[i].Metadata.CorrelationId);
            read[i].Metadata.CausationId.Should().Be(events[i].Metadata.CausationId);
        }
    }

    public static async Task A_failing_insert_stores_none_of_the_append(IEventStoreAdapter adapter)
    {
        var id = new StreamId($"append-{Guid.NewGuid():N}");
        var ok = Raw("First", new byte[] { 1 }, EventMetadata.New("First"));
        // event_type is NOT NULL, so the second insert of the batch fails after the first succeeded.
        var broken = new RawEvent(StreamPosition.Start, null!, new byte[] { 2 }, EventMetadata.New("Broken"));

        var act = async () => await adapter.AppendAsync(id, new[] { ok, broken }.AsMemory(), StreamPosition.Start);

        await act.Should().ThrowAsync<Exception>();
        (await ReadAllAsync(adapter, id)).Should().BeEmpty("the first insert is rolled back with the failed one");

        var retry = await adapter.AppendAsync(id, new[] { ok }.AsMemory(), StreamPosition.Start);
        retry.IsSuccess.Should().BeTrue("the stream is still at version 0");
        retry.Value.NextExpectedVersion.Value.Should().Be(1);
    }

    private static RawEvent Raw(string type, ReadOnlyMemory<byte> payload, EventMetadata metadata)
        => new(StreamPosition.Start, type, payload, metadata);

    private static async Task<List<RawEvent>> ReadAllAsync(IEventStoreAdapter adapter, StreamId id)
    {
        var list = new List<RawEvent>();
        await foreach (var e in adapter.ReadAsync(id, StreamPosition.Start))
            list.Add(e);
        return list;
    }
}
