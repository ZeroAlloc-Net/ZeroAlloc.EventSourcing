using AwesomeAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>The event every dead-letter contract test writes and expects to read back.</summary>
public sealed record DeadLetterTestEvent(string OrderId, int Quantity)
{
    /// <summary>The stored event type name <see cref="DeadLetterTestEventTypeRegistry"/> resolves.</summary>
    public const string TypeName = "DeadLetterTestEvent";
}

/// <summary>
/// Resolves <see cref="DeadLetterTestEvent.TypeName"/> and any extra alias to
/// <see cref="DeadLetterTestEvent"/>, for stores that deserialize on read.
/// </summary>
public sealed class DeadLetterTestEventTypeRegistry(params string[] aliases) : IEventTypeRegistry
{
    public bool TryGetType(string eventType, out Type? type)
    {
        type = string.Equals(eventType, DeadLetterTestEvent.TypeName, StringComparison.Ordinal)
               || aliases.Contains(eventType, StringComparer.Ordinal)
            ? typeof(DeadLetterTestEvent)
            : null;
        return type is not null;
    }

    public string GetTypeName(Type type) =>
        type == typeof(DeadLetterTestEvent)
            ? DeadLetterTestEvent.TypeName
            : throw new ArgumentException($"Unregistered type {type}.", nameof(type));
}

public abstract class DeadLetterStoreContractTests
{
    protected abstract IDeadLetterStore CreateStore();

    private static EventEnvelope MakeEnvelope(int quantity = 1) =>
        new(new StreamId("test-stream"), new StreamPosition(1), new DeadLetterTestEvent("order-" + quantity, quantity),
            new EventMetadata(Guid.NewGuid(), DeadLetterTestEvent.TypeName, DateTimeOffset.UtcNow, null, null));

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

    // The SQL stores used to return the serialized payload as a byte[] here, so replay code
    // written against the in-memory store failed at a cast in production.
    [Fact]
    public async Task ReadAllAsync_ReturnsTheEventObject()
    {
        var store = CreateStore();
        var envelope = MakeEnvelope(3);

        await store.WriteAsync("consumer-1", envelope, new InvalidOperationException("boom"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Should().ContainSingle()
            .Which.Envelope.Event.Should().BeOfType<DeadLetterTestEvent>()
            .Which.Should().Be(new DeadLetterTestEvent("order-3", 3));
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
        var metadata = new EventMetadata(Guid.NewGuid(), DeadLetterTestEvent.TypeName, occurredAt, Guid.NewGuid(), Guid.NewGuid());
        var envelope = new EventEnvelope(new StreamId("test-stream"), new StreamPosition(7), new DeadLetterTestEvent("o", 1), metadata);

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
        await store.WriteAsync("c1", MakeEnvelope(1), new Exception("e1"));
        await store.WriteAsync("c2", MakeEnvelope(2), new Exception("e2"));

        var results = new List<DeadLetterEntry>();
        await foreach (var e in store.ReadAllAsync())
            results.Add(e);

        results.Select(r => r.Envelope.Event).Should().Equal(
            new DeadLetterTestEvent("order-1", 1), new DeadLetterTestEvent("order-2", 2));
    }
}

/// <summary>
/// Contract for dead-letter stores that serialize the event on write and deserialize it on read.
/// <see cref="CreateStore"/> must return a store that resolves types through a
/// <see cref="DeadLetterTestEventTypeRegistry"/>.
/// </summary>
public abstract class SerializingDeadLetterStoreContractTests : DeadLetterStoreContractTests
{
    // A row whose event type the registry no longer knows cannot become an event object. Skipping
    // it would hide it from monitoring and replay, so the read fails and names the row instead.
    [Fact]
    public async Task ReadAllAsync_UnknownEventType_ThrowsNamingTheRow()
    {
        var store = CreateStore();
        var envelope = new EventEnvelope(
            new StreamId("unknown-stream"), new StreamPosition(42), new DeadLetterTestEvent("o", 1),
            new EventMetadata(Guid.NewGuid(), "UnregisteredEvent", DateTimeOffset.UtcNow, null, null));
        await store.WriteAsync("consumer-1", envelope, new InvalidOperationException("boom"));

        var read = async () =>
        {
            await foreach (var _ in store.ReadAllAsync())
            {
            }
        };

        var thrown = await read.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("'UnregisteredEvent'")
            .And.Contain("'unknown-stream'")
            .And.Contain("position 42");
    }
}
