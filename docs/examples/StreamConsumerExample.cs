using ZeroAlloc.EventSourcing;

// Example: Basic stream consumer usage
class StreamConsumerExample
{
    public static async Task Main()
    {
        // Setup
        var eventStore = new InMemoryEventStore(/* ... */);
        var checkpointStore = new InMemoryCheckpointStore();
        var options = new StreamConsumerOptions
        {
            BatchSize = 100,
            MaxRetries = 3,
            ErrorStrategy = ErrorHandlingStrategy.FailFast,
            CommitStrategy = CommitStrategy.AfterBatch
        };

        // Create consumer
        var consumer = new StreamConsumer(
            eventStore,
            checkpointStore,
            consumerId: "my-consumer",
            options
        );

        // Process events
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            Console.WriteLine($"Processing event: {envelope.Event}");
            // Your business logic here
            await Task.CompletedTask;
        });

        // Get current position
        var position = await consumer.GetPositionAsync();
        Console.WriteLine($"Last processed position: {position?.Value}");

        // Manual commit (if using CommitStrategy.Manual)
        await consumer.CommitAsync();

        // Reset for replay
        await consumer.ResetPositionAsync(StreamPosition.Start);
    }
}
