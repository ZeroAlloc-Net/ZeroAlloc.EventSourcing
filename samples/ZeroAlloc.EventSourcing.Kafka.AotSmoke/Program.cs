// samples/ZeroAlloc.EventSourcing.Kafka.AotSmoke/Program.cs
// The Kafka consumer under PublishAot=true, against the broker at ZA_ES_AOTSMOKE_KAFKA, a
// bootstrap server list. Validates:
//   - UseKafkaManualPartitions resolves a KafkaManualPartitionConsumer from DI
//   - it consumes events produced with the event-type header, maps them to envelopes,
//     and writes the partition checkpoint
//   - KafkaEventStoreHealthCheck reports the broker healthy
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.AotSmoke.Shared;
using ZeroAlloc.EventSourcing.Kafka;

var brokerTimeout = TimeSpan.FromSeconds(60);

try
{
    var bootstrapServers = Smoke.RequireEnvironment("ZA_ES_AOTSMOKE_KAFKA");
    var topic = Smoke.Unique("smoke");

    await CreateTopicAsync(bootstrapServers, topic, brokerTimeout).ConfigureAwait(false);

    using (var producer = new ProducerBuilder<string, byte[]>(
        new ProducerConfig { BootstrapServers = bootstrapServers }).Build())
    {
        var serializer = new PingSerializer();
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var headers = new Headers
            {
                { "event-type", Encoding.UTF8.GetBytes(nameof(Ping)) },
                { "event-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            };
            await producer.ProduceAsync(
                new TopicPartition(topic, new Partition(0)),
                new Message<string, byte[]>
                {
                    Key = "smoke-stream",
                    Value = serializer.Serialize(new Ping(sequence)).ToArray(),
                    Headers = headers,
                }).ConfigureAwait(false);
        }
    }

    var services = new ServiceCollection();
    services.AddSingleton<IEventSerializer, PingSerializer>();
    services.AddSingleton<IEventTypeRegistry, PingRegistry>();
    services.AddEventSourcing()
        .UseInMemoryCheckpointStore()
        .UseKafkaManualPartitions(new KafkaManualPartitionOptions
        {
            BootstrapServers = bootstrapServers,
            Topic = topic,
            ConsumerId = "smoke-consumer",
            GroupId = Smoke.Unique("smoke-group"),
            Partitions = [0],
            // The consumer returns once a poll comes back empty, after both events are read.
            PollTimeout = TimeSpan.FromSeconds(5),
        });
    await using var provider = services.BuildServiceProvider();

    var received = new List<(string Stream, long Sequence)>();
    using (var consumer = provider.GetRequiredService<KafkaManualPartitionConsumer>())
    using (var cts = new CancellationTokenSource(brokerTimeout))
    {
        await consumer.ConsumeAsync((envelope, _) =>
        {
            if (envelope.Event is Ping ping)
                received.Add((envelope.StreamId.Value, ping.Sequence));
            return Task.CompletedTask;
        }, cts.Token).ConfigureAwait(false);
    }

    Smoke.Check(
        received.Count == 2
            && received[0] == ("smoke-stream", 1)
            && received[1] == ("smoke-stream", 2),
        $"Kafka: consumed {string.Join(", ", received)}, expected Ping 1 and 2 on smoke-stream");

    var checkpoint = await provider.GetRequiredService<ICheckpointStore>()
        .ReadAsync("smoke-consumer:p0").ConfigureAwait(false);
    Smoke.Check(checkpoint is not null, "Kafka: no checkpoint was written for partition 0");

    using var healthCheck = new KafkaEventStoreHealthCheck(bootstrapServers);
    var health = await healthCheck.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
    Smoke.Check(
        health.Status == HealthStatus.Healthy,
        $"Kafka: the health check reported {health.Status}: {health.Description} {health.Exception?.Message}");
}
catch (SmokeFailure e)
{
    Console.Error.WriteLine($"Kafka AOT smoke FAIL: {e.Message}");
    return 1;
}

Console.WriteLine("Kafka AOT smoke PASS");
return 0;

// A broker can accept connections before it has elected a controller, and topic creation fails
// until it has one, so this retries within a bound instead of failing on the first attempt.
static async Task CreateTopicAsync(string bootstrapServers, string topic, TimeSpan timeout)
{
    using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        try
        {
            await admin.CreateTopicsAsync(
                [new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }],
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
            return;
        }
        catch (CreateTopicsException e) when (e.Results.TrueForAll(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // An earlier attempt timed out on the client after the broker had created the topic.
            return;
        }
        catch (KafkaException e) when (DateTime.UtcNow < deadline)
        {
            Console.WriteLine($"Kafka: topic creation failed, retrying: {e.Message}");
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }
}
