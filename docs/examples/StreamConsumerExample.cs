using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Examples.StreamConsumers;

// Example: Basic stream consumer usage.
// This file is compiled and run by the test suite, so it only uses the public API.

public record TemperatureRecorded(string Sensor, double Celsius);

public static class StreamConsumerExample
{
    public sealed record Outcome(IReadOnlyList<string> Processed, StreamPosition? PositionAfterRun, StreamPosition? PositionAfterReset);

    public static async Task<Outcome> RunAsync()
    {
        // Setup: an in-memory event store with a few events in two streams
        var eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new TemperatureEventTypeRegistry());

        await eventStore.AppendAsync(
            new StreamId("sensor-kitchen"),
            new object[] { new TemperatureRecorded("kitchen", 21.5), new TemperatureRecorded("kitchen", 22.0) },
            StreamPosition.Start);
        await eventStore.AppendAsync(
            new StreamId("sensor-garage"),
            new object[] { new TemperatureRecorded("garage", 12.0) },
            StreamPosition.Start);

        var checkpointStore = new InMemoryCheckpointStore();
        var options = new StreamConsumerOptions
        {
            BatchSize = 100,
            MaxRetries = 3,
            ErrorStrategy = ErrorHandlingStrategy.FailFast,
            CommitStrategy = CommitStrategy.AfterBatch
        };

        // Create consumer. Without a streamId it reads the global stream: every event in the
        // store, in append order.
        var consumer = new StreamConsumer(
            eventStore,
            checkpointStore,
            consumerId: "my-consumer",
            options);

        // Process events. ConsumeAsync returns when the consumer has caught up.
        var processed = new List<string>();
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Console.WriteLine($"Processing event: {envelope.Event}");
            // Your business logic here
            if (envelope.Event is TemperatureRecorded e)
                processed.Add(FormattableString.Invariant($"{e.Sensor}:{e.Celsius}"));
            await Task.CompletedTask;
        });

        // Get current position (the checkpoint written after the batch)
        var position = await consumer.GetPositionAsync();
        Console.WriteLine($"Last processed position: {position?.Value}");

        // Manual commit (if using CommitStrategy.Manual)
        await consumer.CommitAsync();

        // Reset for replay: the next ConsumeAsync starts from the beginning again
        await consumer.ResetPositionAsync(StreamPosition.Start);
        var positionAfterReset = await consumer.GetPositionAsync();

        return new Outcome(processed, position, positionAfterReset);
    }

    // Maps event type names to CLR types. For an aggregate, the source generator emits this.
    private sealed class TemperatureEventTypeRegistry : IEventTypeRegistry
    {
        public bool TryGetType(string eventType, out Type? type)
        {
            type = eventType == nameof(TemperatureRecorded) ? typeof(TemperatureRecorded) : null;
            return type is not null;
        }

        public string GetTypeName(Type type) => type.Name;
    }

    // A reflection-based JSON serializer keeps the example short. In an application, use the
    // built-in ZeroAllocEventSerializer that AddEventSourcing() registers; it is AOT-safe.
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => JsonSerializer.Deserialize(payload.Span, eventType)!;
    }
}
