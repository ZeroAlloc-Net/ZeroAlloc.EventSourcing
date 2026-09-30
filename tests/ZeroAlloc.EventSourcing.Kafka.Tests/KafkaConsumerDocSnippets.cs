using Testcontainers.Kafka;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

/// <summary>
/// The Testcontainers sketch from docs/core-concepts/kafka-consumer.md, compiled against the real
/// Testcontainers and Kafka package APIs. It is not run: the integration tests in this project
/// cover the same steps. When the snippet changes in the docs, change it here as well.
/// </summary>
internal static class KafkaConsumerDocSnippets
{
    public static async Task<KafkaManualPartitionOptions> IntegrationTestingSketch()
    {
        // --- snippet: "Integration Testing with Testcontainers" ---
        // Sketch: the container gives you the bootstrap address for the options
        var kafka = new KafkaBuilder("confluentinc/cp-kafka:7.5.0").Build();
        await kafka.StartAsync();

        var options = new KafkaManualPartitionOptions
        {
            BootstrapServers = kafka.GetBootstrapAddress(),
            Topic = "test",
            ConsumerId = "test-consumer",
            Partitions = [0]
        };
        // --- end snippet ---
        return options;
    }
}
