using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.InMemory;

/// <summary>
/// Health check for the in-memory event store adapter. Always reports <see cref="HealthStatus.Healthy"/>
/// since the in-memory store has no external connectivity to verify.
/// </summary>
public sealed class InMemoryEventStoreHealthCheck : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy());
}
