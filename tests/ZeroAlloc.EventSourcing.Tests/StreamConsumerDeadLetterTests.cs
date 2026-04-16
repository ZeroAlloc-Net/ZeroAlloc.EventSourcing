using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerDeadLetterTests
{
    private readonly IEventStore _eventStore;
    private readonly InMemoryCheckpointStore _checkpointStore = new();

    public StreamConsumerDeadLetterTests()
    {
        _eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new StreamConsumerTestEventTypeRegistry());
    }

    [Fact]
    public async Task ConsumeAsync_WithDeadLetterStrategy_RoutesFailedEventToStore()
    {
        var streamId = new StreamId("dl-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 42 } }.AsMemory(), StreamPosition.Start);

        var deadLetterStore = new InMemoryDeadLetterStore();
        var options = new StreamConsumerOptions
        {
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.DeadLetter
        };
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-dl-test", options, streamId, deadLetterStore);

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            throw new InvalidOperationException("handler-failure");
        }, default);

        var entries = new List<DeadLetterEntry>();
        await foreach (var e in deadLetterStore.ReadAllAsync())
            entries.Add(e);

        entries.Should().HaveCount(1);
        entries[0].ConsumerId.Should().Be("consumer-dl-test");
        entries[0].ExceptionType.Should().Be(nameof(InvalidOperationException));
        entries[0].ExceptionMessage.Should().Be("handler-failure");
    }

    [Fact]
    public async Task ConsumeAsync_DeadLetterWithoutStore_Throws()
    {
        var streamId = new StreamId("dl-nostore-stream");
        await _eventStore.AppendAsync(streamId, new object[] { new TestEvent { Value = 1 } }.AsMemory(), StreamPosition.Start);

        var options = new StreamConsumerOptions
        {
            MaxRetries = 0,
            ErrorStrategy = ErrorHandlingStrategy.DeadLetter
        };
        // No dead-letter store passed
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-dl-nostore", options, streamId);

        Func<Task> action = async () =>
        {
            await consumer.ConsumeAsync(async (envelope, ct) =>
            {
                throw new InvalidOperationException("handler-failure");
            }, default);
        };

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dead-letter store*");
    }
}
