using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Health check for Kafka-based event store adapters.
/// Fetches broker metadata — a lightweight connectivity check with no side effects.
/// </summary>
public sealed class KafkaEventStoreHealthCheck : IHealthCheck, IDisposable
{
    private readonly IAdminClient _adminClient;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Initializes the health check with the given bootstrap servers.</summary>
    public KafkaEventStoreHealthCheck(string bootstrapServers)
    {
        ArgumentNullException.ThrowIfNull(bootstrapServers);
        _adminClient = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = bootstrapServers })
            .Build();
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var metadata = _adminClient.GetMetadata(MetadataTimeout);
                return HealthCheckResult.Healthy($"Connected. Brokers: {metadata.Brokers.Count}");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy(ex.Message, ex);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => _adminClient.Dispose();
}
