using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerIntegrationTests : IAsyncLifetime
{
    private EventStore _eventStore = null!;
    private InMemoryCheckpointStore _checkpointStore = null!;

    public async Task InitializeAsync()
    {
        var adapter = new InMemoryEventStoreAdapter();
        var serializer = new JsonEventSerializer();
        var registry = new TestEventTypeRegistry();
        _eventStore = new EventStore(adapter, serializer, registry);
        _checkpointStore = new InMemoryCheckpointStore();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CompleteWorkflow_ProducesConsumedEvents()
    {
        var streamId = new StreamId("orders");

        // Produce events
        var result1 = await _eventStore.AppendAsync(streamId, new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent("1", 100m) }), StreamPosition.Start);
        result1.IsSuccess.Should().BeTrue();

        var result2 = await _eventStore.AppendAsync(streamId, new ReadOnlyMemory<object>(new[] { (object)new OrderShippedEvent("1", "TRACK-001") }), result1.Value.NextExpectedVersion);
        result2.IsSuccess.Should().BeTrue();

        var result3 = await _eventStore.AppendAsync(streamId, new ReadOnlyMemory<object>(new[] { (object)new OrderCancelledEvent("1") }), result2.Value.NextExpectedVersion);
        result3.IsSuccess.Should().BeTrue();

        // Consume events
        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "order-processor", new StreamConsumerOptions(), streamId);
        var events = new List<object>();

        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            events.Add(envelope.Event);
            await Task.CompletedTask;
        });

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<OrderPlacedEvent>();
        events[1].Should().BeOfType<OrderShippedEvent>();
        events[2].Should().BeOfType<OrderCancelledEvent>();
    }

    [Fact]
    public async Task MultipleConsumers_ProcessIndependently()
    {
        var streamId = new StreamId("events");
        var result1 = await _eventStore.AppendAsync(streamId, new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent("1", 100m) }), StreamPosition.Start);
        result1.IsSuccess.Should().BeTrue();

        var result2 = await _eventStore.AppendAsync(streamId, new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent("2", 200m) }), result1.Value.NextExpectedVersion);
        result2.IsSuccess.Should().BeTrue();

        var consumer1Events = new List<decimal>();
        var consumer2Events = new List<decimal>();

        var consumer1 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-a", new StreamConsumerOptions(), streamId);
        var consumer2 = new StreamConsumer(_eventStore, _checkpointStore, "consumer-b", new StreamConsumerOptions(), streamId);

        await consumer1.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is OrderPlacedEvent ope) consumer1Events.Add(ope.Amount);
            await Task.CompletedTask;
        });

        await consumer2.ConsumeAsync(async (envelope, ct) =>
        {
            if (envelope.Event is OrderPlacedEvent ope) consumer2Events.Add(ope.Amount);
            await Task.CompletedTask;
        });

        consumer1Events.Should().Equal(100m, 200m);
        consumer2Events.Should().Equal(100m, 200m);
    }

    [Fact]
    public async Task ConsumeAsync_WithCancellation_StopsProcessing()
    {
        var streamId = new StreamId("test-stream");
        StreamPosition nextVersion = StreamPosition.Start;
        for (int i = 0; i < 100; i++)
        {
            var result = await _eventStore.AppendAsync(
                streamId,
                new ReadOnlyMemory<object>(new[] { (object)new OrderPlacedEvent($"order-{i}", i) }),
                nextVersion
            );
            result.IsSuccess.Should().BeTrue();
            nextVersion = result.Value.NextExpectedVersion;
        }

        var consumer = new StreamConsumer(_eventStore, _checkpointStore, "consumer-cancel", new StreamConsumerOptions { BatchSize = 10 }, streamId);
        var processedCount = 0;
        var cts = new CancellationTokenSource();

        try
        {
            await consumer.ConsumeAsync(async (envelope, ct) =>
            {
                processedCount++;
                if (processedCount >= 25)
                    cts.Cancel();
                await Task.CompletedTask;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Should have processed around 25-30 events (batches are processed together)
        processedCount.Should().BeGreaterThanOrEqualTo(25);
        processedCount.Should().BeLessThan(100);
    }
}
