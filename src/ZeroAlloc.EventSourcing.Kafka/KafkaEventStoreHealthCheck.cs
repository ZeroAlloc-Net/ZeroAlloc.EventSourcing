using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Health check for Kafka-based event store adapters.
/// Fetches broker metadata — a lightweight connectivity check with no side effects.
/// </summary>
public sealed class KafkaEventStoreHealthCheck : IHealthCheck
{
    private readonly string _bootstrapServers;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Initializes the health check with the given bootstrap servers.</summary>
    public KafkaEventStoreHealthCheck(string bootstrapServers)
        => _bootstrapServers = bootstrapServers ?? throw new ArgumentNullException(nameof(bootstrapServers));

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var adminClient = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = _bootstrapServers })
                .Build();

            var metadata = adminClient.GetMetadata(MetadataTimeout);
            return Task.FromResult(
                HealthCheckResult.Healthy($"Connected. Brokers: {metadata.Brokers.Count}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(ex.Message, ex));
        }
    }
}
