using FluentAssertions;
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
