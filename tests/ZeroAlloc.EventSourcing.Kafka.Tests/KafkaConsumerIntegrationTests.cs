using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using AwesomeAssertions;
using Testcontainers.Kafka;
using Xunit;
using ZeroAlloc.EventSourcing.Kafka;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

// ── Test event records ────────────────────────────────────────────────────────
// Defined at namespace level (not file-scoped) so typeof(T).Name is a stable
// simple name. The event-type header in Kafka messages must match the name
// registered in the test's SimpleEventTypeRegistry.

internal record IntegrationTestEvent(string Name);
internal record IntegrationTestEvent2(string Name);

// ── Shared test infrastructure ────────────────────────────────────────────────

/// <summary>
/// Simple IEventTypeRegistry with runtime registration, for integration tests.
/// Avoids the source-generated registry dependency.
/// </summary>
internal sealed class SimpleEventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _nameToType = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _typeToName = new();

    public void Register<T>(string name)
    {
        _nameToType[name]      = typeof(T);
        _typeToName[typeof(T)] = name;
    }

    public bool TryGetType(string eventType, out Type? type)
        => _nameToType.TryGetValue(eventType, out type);

    public string GetTypeName(Type type)
        => _typeToName.TryGetValue(type, out var name)
           ? name
           : throw new InvalidOperationException($"Type {type.FullName} is not registered.");
}

/// <summary>
/// IEventSerializer backed by System.Text.Json, for integration tests.
/// </summary>
internal sealed class SystemTextJsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event, typeof(TEvent));

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)
           ?? throw new InvalidOperationException(
               $"Deserialization of {eventType.FullName} returned null.");
}

/// <summary>Shared Kafka container, admin and producer helpers for integration tests.</summary>
internal static class KafkaIntegrationHelpers
{
    private const string KafkaImage = "confluentinc/cp-kafka:7.5.0";

    /// <summary>How long a started broker gets to elect a controller before the fixture fails.</summary>
    public static readonly TimeSpan ControllerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Starts a Kafka container and returns once its broker has elected a controller, so topics
    /// can be created on it.
    /// </summary>
    /// <remarks>
    /// The container reports started once the broker accepts connections, which can be before the
    /// broker has a controller. An admin client that asks for the controller then refreshes its
    /// metadata once, sees none, and looks again only on a broker state change. When none comes,
    /// <c>CreateTopicsAsync</c> fails with "Failed while waiting for controller: Local: Timed out"
    /// after its full request timeout, even when the controller is elected a moment later.
    /// </remarks>
    public static async Task<KafkaContainer> StartKafkaAsync()
    {
        var kafka = new KafkaBuilder(KafkaImage).Build();
        try
        {
            await kafka.StartAsync();
            await WaitForControllerAsync(kafka.GetBootstrapAddress(), ControllerTimeout);
            return kafka;
        }
        catch
        {
            await kafka.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Asks the broker for its cluster description until it names a controller. Each call sends a
    /// fresh request, unlike an admin operation that waits on the client's cached metadata.
    /// </summary>
    /// <exception cref="TimeoutException">The broker named no controller within <paramref name="timeout"/>.</exception>
    public static async Task WaitForControllerAsync(string bootstrapServers, TimeSpan timeout)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        var elapsed   = Stopwatch.StartNew();
        var attempts  = 0;
        Exception? lastError = null;

        while (true)
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The Kafka broker at {bootstrapServers} named no controller within {timeout.TotalSeconds:0} s "
                    + $"({attempts} attempts), so no topic can be created on it. "
                    + (lastError is null
                        ? "The broker answered every request without a controller."
                        : $"The last request failed: {lastError.Message}"),
                    lastError);
            }

            attempts++;
            try
            {
                var cluster = await admin.DescribeClusterAsync(new DescribeClusterOptions
                {
                    RequestTimeout = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5),
                });
                if (cluster.Controller is not null)
                    return;
                lastError = null;
            }
            catch (KafkaException e)
            {
                lastError = e;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    public static async Task CreateTopicAsync(
        string bootstrapServers, string topic, int numPartitions = 1)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        await admin.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name              = topic,
                NumPartitions     = numPartitions,
                ReplicationFactor = 1,
            }
        ]);
    }

    /// <summary>
    /// Produces a single event. The <paramref name="eventTypeName"/> is written as the
    /// <c>event-type</c> header and must match the name registered in the test's registry.
    /// </summary>
    public static async Task ProduceAsync<TEvent>(
        string bootstrapServers,
        string topic,
        int partition,
        TEvent evt,
        string eventTypeName)
        where TEvent : notnull
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = bootstrapServers }).Build();

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(evt, typeof(TEvent));
        var headers = new Headers();
        headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes(eventTypeName));
        headers.Add("event-id",   System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

        await producer.ProduceAsync(
            new TopicPartition(topic, new Partition(partition)),
            new Message<string, byte[]>
            {
                Key     = "stream-1",
                Value   = payload,
                Headers = headers,
            });

    }
}

/// <summary>
/// The controller wait fails within its bound, with a message that says why, when no broker ever
/// answers. It needs no Docker: nothing listens on the address.
/// </summary>
public sealed class KafkaIntegrationHelpersTests
{
    [Fact]
    public async Task WaitForControllerAsync_NoBroker_FailsWithinItsBound()
    {
        const string Unreachable = "127.0.0.1:1";
        var elapsed = Stopwatch.StartNew();

        var act = () => KafkaIntegrationHelpers.WaitForControllerAsync(Unreachable, TimeSpan.FromSeconds(3));

        var thrown = await act.Should().ThrowAsync<TimeoutException>();
        thrown.Which.Message.Should().Contain(Unreachable).And.Contain("named no controller within 3 s");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }
}

// ── Manual partition consumer integration tests ───────────────────────────────

/// <summary>
/// Integration tests for <see cref="KafkaManualPartitionConsumer"/>.
/// Requires Docker. Tag: [Trait("Category", "Integration")].
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaManualPartitionConsumerIntegrationTests : IAsyncLifetime
{
    private KafkaContainer _kafka = null!;
    private string _bootstrapServers = null!;

    public async Task InitializeAsync()
    {
        _kafka = await KafkaIntegrationHelpers.StartKafkaAsync();
        _bootstrapServers = _kafka.GetBootstrapAddress();
    }

    public async Task DisposeAsync() => await _kafka.DisposeAsync();

    /// <summary>
    /// Verifies that the manual-partition consumer reads events from all configured
    /// partitions and writes per-partition checkpoints.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ProcessesEventsFromMultiplePartitions()
    {
        const string EventTypeName = "IntegrationTestEvent";

        var topic      = $"test-{Guid.NewGuid():N}";
        var consumerId = "manual-consumer";
        var groupId    = $"manual-group-{Guid.NewGuid():N}";

        await KafkaIntegrationHelpers.CreateTopicAsync(_bootstrapServers, topic, numPartitions: 2);

        var checkpoints = new InMemoryCheckpointStore();
        var serializer  = new SystemTextJsonEventSerializer();
        var registry    = new SimpleEventTypeRegistry();
        registry.Register<IntegrationTestEvent>(EventTypeName);

        // Produce one event on each partition BEFORE starting the consumer so that
        // the messages are available as soon as the consumer assigns its partitions.
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic,
            partition: 0, new IntegrationTestEvent("from-p0"), EventTypeName);
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic,
            partition: 1, new IntegrationTestEvent("from-p1"), EventTypeName);

        // Small delay to ensure Kafka has fully committed the messages to storage
        // before the consumer connects.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var received = new List<string>();
        var options  = new KafkaManualPartitionOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic            = topic,
            ConsumerId       = consumerId,
            // GroupId is required by Confluent.Kafka even for manual partition assignment.
            GroupId          = groupId,
            Partitions       = [0, 1],
            // After consuming the pre-produced messages the next Consume() returns null
            // (empty batch) → consumer exits naturally.
            PollTimeout      = TimeSpan.FromSeconds(3),
        };

        using var consumer = new KafkaManualPartitionConsumer(options, checkpoints, serializer, registry);
        using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await consumer.ConsumeAsync((env, _) =>
        {
            if (env.Event is IntegrationTestEvent e)
                received.Add(e.Name);
            return Task.CompletedTask;
        }, cts.Token);

        received.Should().HaveCount(2);
        received.Should().Contain("from-p0");
        received.Should().Contain("from-p1");

        // Both per-partition checkpoints must have been written.
        var cp0 = await checkpoints.ReadAsync($"{consumerId}:p0", CancellationToken.None);
        var cp1 = await checkpoints.ReadAsync($"{consumerId}:p1", CancellationToken.None);
        cp0.Should().NotBeNull("checkpoint for partition 0 should be written");
        cp1.Should().NotBeNull("checkpoint for partition 1 should be written");
        // Kafka offsets are 0-based; a valid checkpoint must be a non-negative integer.
        cp0!.Value.Value.Should().BeGreaterThanOrEqualTo(0);
        cp1!.Value.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Verifies that the manual-partition consumer resumes from its checkpoint on restart
    /// and does not replay already-processed events.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ResumesFromCheckpoint_AfterStop()
    {
        const string EventTypeName = "IntegrationTestEvent";

        var topic      = $"test-{Guid.NewGuid():N}";
        var consumerId = "manual-resume-consumer";
        var groupId    = $"manual-resume-group-{Guid.NewGuid():N}";

        await KafkaIntegrationHelpers.CreateTopicAsync(_bootstrapServers, topic, numPartitions: 1);

        var checkpoints = new InMemoryCheckpointStore();
        var serializer  = new SystemTextJsonEventSerializer();
        var registry    = new SimpleEventTypeRegistry();
        registry.Register<IntegrationTestEvent>(EventTypeName);

        for (int i = 1; i <= 3; i++)
            await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
                new IntegrationTestEvent($"event-{i}"), EventTypeName);

        // Allow Kafka to fully commit the messages before the consumer connects.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var options = new KafkaManualPartitionOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic            = topic,
            ConsumerId       = consumerId,
            GroupId          = groupId,
            Partitions       = [0],
            PollTimeout      = TimeSpan.FromSeconds(3),
        };

        // First run: consume all 3 events; exits naturally when topic is drained.
        var firstRun = new List<string>();
        using (var c1 = new KafkaManualPartitionConsumer(options, checkpoints, serializer, registry))
        using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await c1.ConsumeAsync((env, _) =>
            {
                if (env.Event is IntegrationTestEvent ev) firstRun.Add(ev.Name);
                return Task.CompletedTask;
            }, cts1.Token);
        }

        firstRun.Should().HaveCount(3);

        // Produce 2 more events AFTER the first consumer stops.
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
            new IntegrationTestEvent("event-4"), EventTypeName);
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
            new IntegrationTestEvent("event-5"), EventTypeName);

        // Second run: the checkpoint store holds the last-processed offset.
        // The consumer seeks to that offset (at-least-once: may re-read it),
        // then reads the 2 new events.
        var secondRun = new List<string>();
        using (var c2 = new KafkaManualPartitionConsumer(options, checkpoints, serializer, registry))
        using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await c2.ConsumeAsync((env, _) =>
            {
                if (env.Event is IntegrationTestEvent ev) secondRun.Add(ev.Name);
                return Task.CompletedTask;
            }, cts2.Token);
        }

        // The checkpoint is honoured: at most 3 events (last of first run + 2 new),
        // not all 5 from the beginning.
        secondRun.Count.Should().BeLessThanOrEqualTo(3,
            "checkpoint must prevent replaying all 5 events from offset 0");
        secondRun.Should().Contain("event-4");
        secondRun.Should().Contain("event-5");
    }
}

// ── Consumer group consumer integration tests ─────────────────────────────────

/// <summary>
/// Integration tests for <see cref="KafkaConsumerGroupConsumer"/>.
/// Requires Docker. Tag: [Trait("Category", "Integration")].
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaConsumerGroupConsumerIntegrationTests : IAsyncLifetime
{
    private KafkaContainer _kafka = null!;
    private string _bootstrapServers = null!;

    public async Task InitializeAsync()
    {
        _kafka = await KafkaIntegrationHelpers.StartKafkaAsync();
        _bootstrapServers = _kafka.GetBootstrapAddress();
    }

    public async Task DisposeAsync() => await _kafka.DisposeAsync();

    /// <summary>
    /// Verifies that the consumer-group consumer processes events produced while the
    /// consumer is actively polling. The consumer is cancelled once all events are received.
    ///
    /// <para>
    /// Messages are produced in a background task with a short delay (to let Kafka complete
    /// partition assignment before messages arrive). The consumer keeps polling until it
    /// receives all messages and then cancels itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ProcessesEventsProducedWhileRunning()
    {
        const string EventTypeName = "IntegrationTestEvent2";

        var topic      = $"test-{Guid.NewGuid():N}";
        var groupId    = $"group-{Guid.NewGuid():N}";
        var consumerId = "cg-consumer";

        await KafkaIntegrationHelpers.CreateTopicAsync(_bootstrapServers, topic, numPartitions: 1);

        var checkpoints = new InMemoryCheckpointStore();
        var serializer  = new SystemTextJsonEventSerializer();
        var registry    = new SimpleEventTypeRegistry();
        registry.Register<IntegrationTestEvent2>(EventTypeName);

        var received = new List<string>();
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var options = new KafkaConsumerGroupOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic            = topic,
            GroupId          = groupId,
            ConsumerId       = consumerId,
            // Use a generous poll timeout so a single Consume() call bridges the
            // partition-assignment → message-delivery gap.
            PollTimeout      = TimeSpan.FromSeconds(5),
        };

        using var consumer = new KafkaConsumerGroupConsumer(options, checkpoints, serializer, registry);

        // Produce messages after a brief delay so Kafka has time to process the Subscribe()
        // call and begin partition assignment before messages land in the topic.
        var produceTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            for (int i = 1; i <= 3; i++)
                await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
                    new IntegrationTestEvent2($"event-{i}"), EventTypeName);
        });

        var consumeTask = consumer.ConsumeAsync((env, _) =>
        {
            if (env.Event is IntegrationTestEvent2 e)
            {
                lock (received) received.Add(e.Name);
                // Stop the consumer once all expected events are received.
                if (received.Count >= 3) cts.Cancel();
            }
            return Task.CompletedTask;
        }, cts.Token);

        // Await both tasks; swallow OperationCanceledException from a deliberate cancellation.
        await produceTask;
        try { await consumeTask; } catch (OperationCanceledException) { /* expected */ }

        received.Should().HaveCount(3);
        received.Should().Contain("event-1");
        received.Should().Contain("event-2");
        received.Should().Contain("event-3");
    }

    /// <summary>
    /// Verifies that a second consumer-group consumer instance, given the same
    /// <see cref="InMemoryCheckpointStore"/>, does not replay events already seen.
    ///
    /// <para>
    /// Because <see cref="KafkaConsumerGroupConsumer"/> performs the checkpoint seek in
    /// <see cref="KafkaConsumerBase.DrainPendingSeeksAsync"/> — which runs at the top of
    /// each batch iteration, before partition assignment completes — the first
    /// <c>Consume()</c> call after <c>Subscribe()</c> may return a message from
    /// <c>AutoOffsetReset.Earliest</c> (offset 0). The test therefore asserts the practical
    /// guarantee: new events (produced after the first run) are always seen, and the total
    /// number of events in the second run is smaller than the full topic size, proving that
    /// the consumer did not start over from offset 0 for all messages (at least the seek
    /// took effect for subsequent batches).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_NewEventsAreAlwaysDelivered_OnSecondConsumerInstance()
    {
        const string EventTypeName = "IntegrationTestEvent2";

        var topic      = $"test-{Guid.NewGuid():N}";
        var groupId    = $"group-resume-{Guid.NewGuid():N}";
        var consumerId = "cg-resume-consumer";

        await KafkaIntegrationHelpers.CreateTopicAsync(_bootstrapServers, topic, numPartitions: 1);

        var checkpoints = new InMemoryCheckpointStore();
        var serializer  = new SystemTextJsonEventSerializer();
        var registry    = new SimpleEventTypeRegistry();
        registry.Register<IntegrationTestEvent2>(EventTypeName);

        var baseOptions = new KafkaConsumerGroupOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic            = topic,
            GroupId          = groupId,
            ConsumerId       = consumerId,
            PollTimeout      = TimeSpan.FromSeconds(5),
        };

        // ── First run ─────────────────────────────────────────────────────────
        var firstRun = new List<string>();
        var cts1     = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Produce 3 events in background so the consumer is already polling when they arrive.
        var produceFirst = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            for (int i = 1; i <= 3; i++)
                await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
                    new IntegrationTestEvent2($"event-{i}"), EventTypeName);
        });

        using (var c1 = new KafkaConsumerGroupConsumer(baseOptions, checkpoints, serializer, registry))
        {
            var consumeFirst = c1.ConsumeAsync((env, _) =>
            {
                if (env.Event is IntegrationTestEvent2 e)
                {
                    lock (firstRun) firstRun.Add(e.Name);
                    if (firstRun.Count >= 3) cts1.Cancel();
                }
                return Task.CompletedTask;
            }, cts1.Token);

            await produceFirst;
            try { await consumeFirst; } catch (OperationCanceledException) { /* expected */ }
        }

        firstRun.Should().HaveCount(3, "first consumer must process all 3 initial events");

        // Produce 2 additional events AFTER the first consumer has stopped.
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
            new IntegrationTestEvent2("event-4"), EventTypeName);
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, partition: 0,
            new IntegrationTestEvent2("event-5"), EventTypeName);

        // ── Second run ────────────────────────────────────────────────────────
        var secondRun = new List<string>();
        var cts2      = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using (var c2 = new KafkaConsumerGroupConsumer(baseOptions, checkpoints, serializer, registry))
        {
            var consumeSecond = c2.ConsumeAsync((env, _) =>
            {
                if (env.Event is IntegrationTestEvent2 ev2)
                {
                    lock (secondRun) secondRun.Add(ev2.Name);
                    // Cancel as soon as we have the 2 new events.
                    if (secondRun.Count >= 2) cts2.Cancel();
                }
                return Task.CompletedTask;
            }, cts2.Token);

            try { await consumeSecond; } catch (OperationCanceledException) { /* expected */ }
        }

        // The two new events must always be visible to the second consumer.
        secondRun.Should().Contain("event-4");
        secondRun.Should().Contain("event-5");
        // Upper bound: must not replay all events from the start
        secondRun.Count.Should().BeLessThanOrEqualTo(3,
            "the second consumer must resume from the checkpoint, not replay from offset 0");
    }

    /// <summary>
    /// Verifies that the consumer-group consumer processes events from all partitions of a
    /// multi-partition topic. Events are pre-produced on each partition before the consumer
    /// starts, and <c>AutoOffsetReset.Earliest</c> ensures all messages are read.
    /// The consumer exits naturally after an empty poll (topic is drained).
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ProcessesEventsFromMultiplePartitions_ViaRebalancing()
    {
        var topic      = $"test-{Guid.NewGuid():N}";
        var groupId    = $"group-{Guid.NewGuid():N}";
        var consumerId = "cg-multipartition-consumer";
        var checkpoints = new InMemoryCheckpointStore();
        var serializer  = new SystemTextJsonEventSerializer();
        var registry    = new SimpleEventTypeRegistry();
        registry.Register<IntegrationTestEvent>("TestEvent");

        await KafkaIntegrationHelpers.CreateTopicAsync(_bootstrapServers, topic, numPartitions: 2);

        // Pre-produce 1 event on each partition so messages are available when the consumer starts.
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, 0, new IntegrationTestEvent("p0-event"), "TestEvent");
        await KafkaIntegrationHelpers.ProduceAsync(_bootstrapServers, topic, 1, new IntegrationTestEvent("p1-event"), "TestEvent");

        // Small delay to ensure Kafka has fully committed the messages to storage
        // before the consumer connects.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var options = new KafkaConsumerGroupOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic            = topic,
            GroupId          = groupId,
            ConsumerId       = consumerId,
            ConsumerOptions  = new StreamConsumerOptions { CommitStrategy = CommitStrategy.AfterEvent },
            PollTimeout      = TimeSpan.FromSeconds(5),
        };

        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var consumer = new KafkaConsumerGroupConsumer(options, checkpoints, serializer, registry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await consumer.ConsumeAsync((env, _) =>
        {
            if (env.Event is IntegrationTestEvent e) received.Add(e.Name);
            return Task.CompletedTask;
        }, cts.Token);

        // At-least-once semantics: each event must appear at least once.
        // Rebalancing may cause the consumer to re-read from offset 0, so duplicates are possible.
        received.Should().Contain("p0-event");
        received.Should().Contain("p1-event");
        received.Count.Should().BeGreaterThanOrEqualTo(2, "both partitions must deliver at least one event");
    }
}
