using AwesomeAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public abstract class DeadLetterStoreContractTests
{
    protected abstract IDeadLetterStore CreateStore();

    private static EventEnvelope MakeEnvelope() =>
        new(new StreamId("test-stream"), new StreamPosition(1), new object(),
            new EventMetadata(Guid.NewGuid(), "TestEvent", DateTimeOffset.UtcNow, null, null));

    [Fact]
    public async Task ReadAllAsync_Empty_ReturnsNothing()
    {
        var store = CreateStore();
        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_SingleEntry_CanBeRead()
    {
        var store = CreateStore();
        var envelope = MakeEnvelope();
        var ex = new InvalidOperationException("boom");

        await store.WriteAsync("consumer-1", envelope, ex);

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(1);
        results[0].ConsumerId.Should().Be("consumer-1");
        results[0].ExceptionType.Should().Be(nameof(InvalidOperationException));
        results[0].ExceptionMessage.Should().Be("boom");
        results[0].Envelope.Metadata.EventId.Should().Be(envelope.Metadata.EventId);
    }

    // The SQL stores used to drop the metadata and read back a fresh random EventId, so a
    // dead-lettered event could not be traced back to the event that failed.
    [Fact]
    public async Task WriteAsync_PreservesEventMetadata()
    {
        var store = CreateStore();
        // Whole seconds: PostgreSQL keeps microseconds and SQL Server 100 ns ticks. A non-UTC
        // offset, because Npgsql refuses to write one to a timestamptz column unconverted.
        var occurredAt = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.FromHours(2));
        var metadata = new EventMetadata(Guid.NewGuid(), "TestEvent", occurredAt, Guid.NewGuid(), Guid.NewGuid());
        var envelope = new EventEnvelope(new StreamId("test-stream"), new StreamPosition(7), new object(), metadata);

        await store.WriteAsync("consumer-1", envelope, new InvalidOperationException("boom"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Should().ContainSingle();
        results[0].Envelope.StreamId.Should().Be(envelope.StreamId);
        results[0].Envelope.Position.Should().Be(envelope.Position);
        results[0].Envelope.Metadata.Should().Be(metadata);
    }

    [Fact]
    public async Task WriteAsync_MultipleEntries_AllReadBack()
    {
        var store = CreateStore();
        await store.WriteAsync("c1", MakeEnvelope(), new Exception("e1"));
        await store.WriteAsync("c2", MakeEnvelope(), new Exception("e2"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Should().HaveCount(2);
    }
}
