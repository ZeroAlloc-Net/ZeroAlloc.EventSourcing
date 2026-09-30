using System.Text;
using AwesomeAssertions;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Kafka;
using ZeroAlloc.EventSourcing.Sql;

// The C# in docs/core-concepts/kafka-consumer.md, copied as it appears there between
// "--- snippet ---" markers and compiled against the public API. Most of it needs a Kafka broker
// or a database, so it is only compiled; the parts that do not are run. When a snippet changes in
// the docs, change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.KafkaConsumerDocs;

public interface IEventHandler
{
    Task ProcessAsync(EventEnvelope envelope, CancellationToken ct);
}

public static class KafkaConsumerDocSnippets
{
    public static KafkaConsumerGroupOptions ConfigureTheConsumer()
    {
        // --- snippet: "1. Configure the Consumer" ---
        var options = new KafkaConsumerGroupOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "my-events",
            GroupId = "my-service",
            ConsumerId = "my-service-consumer",
            ConsumerOptions = new StreamConsumerOptions
            {
                BatchSize = 100,
                MaxRetries = 3,
                RetryPolicy = new ExponentialBackoffRetryPolicy(),
                ErrorStrategy = ErrorHandlingStrategy.FailFast,
                CommitStrategy = CommitStrategy.AfterBatch
            }
        };
        // --- end snippet ---
        return options;
    }

    public static async Task CreateAndConsume(
        KafkaConsumerGroupOptions options,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IEventHandler handler,
        CancellationToken cancellationToken)
    {
        // --- snippet: "2. Create the Consumer" ---
        var checkpointStore = new InMemoryCheckpointStore();

        using var consumer = new KafkaConsumerGroupConsumer(options, checkpointStore, serializer, registry);
        // --- end snippet ---

        // --- snippet: "3. Consume Events" ---
        // Processes the messages that are available and returns once a poll finds no new ones
        await consumer.ConsumeAsync(async (envelope, ct) =>
        {
            await handler.ProcessAsync(envelope, ct);
        }, cancellationToken);
        // --- end snippet ---
    }

    public static KafkaManualPartitionConsumer ManualPartitions(
        ICheckpointStore checkpointStore, IEventSerializer serializer, IEventTypeRegistry registry)
    {
        // --- snippet: "Manual Partition Assignment" ---
        var options = new KafkaManualPartitionOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "my-events",
            ConsumerId = "billing-partitions-0-1",
            Partitions = [0, 1]
        };

        var consumer = new KafkaManualPartitionConsumer(options, checkpointStore, serializer, registry);
        // --- end snippet ---
        return consumer;
    }

    public static void DependencyInjection(IServiceCollection services, KafkaConsumerGroupOptions options)
    {
        // --- snippet: "Dependency Injection" ---
        services
            .AddEventSourcing()
            .UseInMemoryCheckpointStore()   // or UsePostgreSqlCheckpointStore(cs) / UseSqlServerCheckpointStore(cs)
            .UseKafkaConsumerGroup(options);

        // Resolve KafkaConsumerGroupConsumer from the container; it needs your IEventSerializer and
        // IEventTypeRegistry registered too
        // --- end snippet ---
    }

    public static async Task Produce(IProducer<string, byte[]> producer, string orderId, Guid correlationId, byte[] payload)
    {
        // --- snippet: "Producer Example" ---
        var headers = new Headers();
        headers.Add("event-type", Encoding.UTF8.GetBytes("OrderCreated"));
        headers.Add("event-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
        headers.Add("occurred-at", Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
        headers.Add("correlation-id", Encoding.UTF8.GetBytes(correlationId.ToString()));

        var message = new Message<string, byte[]>
        {
            Key = orderId,   // becomes the envelope's StreamId; the topic name is used when it is empty
            Value = payload, // serialized with the same format as your IEventSerializer reads
            Headers = headers
        };

        await producer.ProduceAsync("my-events", message);
        // --- end snippet ---
    }

    public static ICheckpointStore[] CheckpointStores(string postgreSqlConnectionString, string sqlServerConnectionString)
    {
        // --- snippet: "Checkpoint Stores" ---
        // In memory: lost on restart
        var inMemory = new InMemoryCheckpointStore();

        // PostgreSQL and SQL Server, from the ZeroAlloc.EventSourcing.Sql package
        var postgreSql = new PostgreSqlCheckpointStore(NpgsqlDataSource.Create(postgreSqlConnectionString));
        var sqlServer = new SqlServerCheckpointStore(sqlServerConnectionString);
        // --- end snippet ---
        return [inMemory, postgreSql, sqlServer];
    }

    public static void CommitStrategies(KafkaConsumerGroupOptions options)
    {
        // --- snippet: "CommitStrategy.AfterEvent" ---
        options.ConsumerOptions.CommitStrategy = CommitStrategy.AfterEvent;  // checkpoint every message
        // --- end snippet ---

        // --- snippet: "CommitStrategy.AfterBatch" ---
        options.ConsumerOptions.CommitStrategy = CommitStrategy.AfterBatch;  // checkpoint after each batch
        options.ConsumerOptions.BatchSize = 100;
        // --- end snippet ---

        // --- snippet: "CommitStrategy.Manual" ---
        options.ConsumerOptions.CommitStrategy = CommitStrategy.Manual;
        // --- end snippet ---
    }

    public static async Task ManualCommit(KafkaConsumerBase consumer, Func<EventEnvelope, CancellationToken, Task> handler)
    {
        // --- snippet: "CommitStrategy.Manual" usage ---
        // Process messages
        await consumer.ConsumeAsync(handler);

        // Explicitly commit when ready
        await consumer.CommitAsync();
        // --- end snippet ---
    }

    public static void ErrorStrategies(KafkaConsumerGroupOptions options)
    {
        // --- snippet: "ErrorHandlingStrategy.FailFast" ---
        options.ConsumerOptions.MaxRetries = 3;
        options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.FailFast;
        // --- end snippet ---

        // --- snippet: "ErrorHandlingStrategy.Skip" ---
        options.ConsumerOptions.MaxRetries = 3;
        options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.Skip;
        // --- end snippet ---
    }

    public static KafkaConsumerGroupConsumer DeadLetter(
        KafkaConsumerGroupOptions options,
        ICheckpointStore checkpointStore,
        IEventSerializer serializer,
        IEventTypeRegistry registry,
        IDeadLetterStore deadLetterStore)
    {
        // --- snippet: "ErrorHandlingStrategy.DeadLetter" ---
        options.ConsumerOptions.ErrorStrategy = ErrorHandlingStrategy.DeadLetter;

        // The consumer writes a message that still fails after the retries to this store
        var consumer = new KafkaConsumerGroupConsumer(options, checkpointStore, serializer, registry, deadLetterStore);
        // --- end snippet ---
        return consumer;
    }

    public static void RetryPolicy(KafkaConsumerGroupOptions options)
    {
        // --- snippet: "Retry Policy" ---
        options.ConsumerOptions.MaxRetries = 3;
        options.ConsumerOptions.RetryPolicy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 30_000);
        // --- end snippet ---
    }

    public static async Task Positions(KafkaConsumerBase consumer)
    {
        // --- snippet: "Get Current Position" ---
        var position = await consumer.GetPositionAsync();
        Console.WriteLine($"Lowest checkpointed offset across the assigned partitions: {position?.Value}");
        // --- end snippet ---

        // --- snippet: "Reset to Start" ---
        await consumer.ResetPositionAsync(StreamPosition.Start);
        // Deletes the checkpoints: the next run starts from the beginning of each partition
        // --- end snippet ---

        // --- snippet: "Reset to Specific Position" ---
        await consumer.ResetPositionAsync(new StreamPosition(100));
        // The next run starts at offset 100 of each assigned partition
        // --- end snippet ---
    }
}

/// <summary>Runs the snippets that need neither a broker nor a database.</summary>
public sealed class KafkaConsumerDocTests
{
    [Fact]
    public void ConfiguredOptions_AreValid()
    {
        var options = KafkaConsumerDocSnippets.ConfigureTheConsumer();

        options.Invoking(o => o.Validate()).Should().NotThrow();
        options.ConsumerOptions.BatchSize.Should().Be(100);
    }

    [Fact]
    public void StrategySnippets_SetTheOptions()
    {
        var options = KafkaConsumerDocSnippets.ConfigureTheConsumer();

        KafkaConsumerDocSnippets.CommitStrategies(options);
        options.ConsumerOptions.CommitStrategy.Should().Be(CommitStrategy.Manual);

        KafkaConsumerDocSnippets.ErrorStrategies(options);
        options.ConsumerOptions.ErrorStrategy.Should().Be(ErrorHandlingStrategy.Skip);
    }

    [Fact]
    public void DependencyInjection_RegistersTheConsumer()
    {
        var services = new ServiceCollection();

        KafkaConsumerDocSnippets.DependencyInjection(services, KafkaConsumerDocSnippets.ConfigureTheConsumer());

        services.Should().Contain(d => d.ServiceType == typeof(KafkaConsumerGroupConsumer));
        services.Should().Contain(d => d.ServiceType == typeof(ICheckpointStore));
    }
}
