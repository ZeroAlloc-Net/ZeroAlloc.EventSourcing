using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.SqlServer;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for the SQL Server event store adapter.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="SqlServerEventStoreAdapter"/> as <see cref="IEventStoreAdapter"/>
    /// and <see cref="EventStore"/> as <see cref="IEventStore"/>.
    /// </summary>
    /// <param name="builder">The event sourcing builder.</param>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <remarks>
    /// <see cref="IEventTypeRegistry"/> must be registered separately — it is a required
    /// dependency of <see cref="EventStore"/> and will cause a resolution
    /// failure if absent.
    /// </remarks>
    public static EventSourcingBuilder UseSqlServerEventStore(
        this EventSourcingBuilder builder,
        string connectionString)
    {
        builder.Services.TryAddSingleton<IEventStoreAdapter>(
            _ => new SqlServerEventStoreAdapter(connectionString));
        builder.Services.TryAddSingleton<IEventStore, EventStore>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqlServerEventStoreHealthCheck"/> with the health check system.
    /// Opens a connection and executes <c>SELECT 1</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="name">Health check registration name. Defaults to <c>sqlserver-event-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    public static IHealthChecksBuilder AddSqlServerEventStore(
        this IHealthChecksBuilder builder,
        string connectionString,
        string name = "sqlserver-event-store",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new SqlServerEventStoreHealthCheck(connectionString),
            failureStatus,
            tags));
}
