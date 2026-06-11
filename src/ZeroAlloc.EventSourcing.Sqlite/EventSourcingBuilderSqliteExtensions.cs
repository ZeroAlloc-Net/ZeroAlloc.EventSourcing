using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZeroAlloc.EventSourcing.Sqlite;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for the SQLite event store adapter.
/// </summary>
public static class EventSourcingBuilderSqliteExtensions
{
    /// <summary>
    /// Registers <see cref="SqliteEventStoreAdapter"/> as <see cref="IEventStoreAdapter"/>
    /// and <see cref="EventStore"/> as <see cref="IEventStore"/>. Optionally bootstraps the
    /// schema via <see cref="SqliteEventStoreAdapter.EnsureSchemaAsync"/> at registration time.
    /// </summary>
    /// <param name="builder">The event sourcing builder.</param>
    /// <param name="connectionString">A valid SQLite connection string (e.g. <c>Data Source=events.db</c>).</param>
    /// <param name="ensureSchema">
    /// When <c>true</c> (default), calls <c>EnsureSchemaAsync</c> during the singleton
    /// factory so the <c>event_store</c> table exists before the adapter is first used.
    /// App startup blocks briefly on this one-time bootstrap.
    /// </param>
    /// <remarks>
    /// <see cref="IEventTypeRegistry"/> must be registered separately — it is a required
    /// dependency of <see cref="EventStore"/> and will cause a resolution failure if absent.
    /// </remarks>
    public static EventSourcingBuilder UseSqliteEventStore(
        this EventSourcingBuilder builder,
        string connectionString,
        bool ensureSchema = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.TryAddSingleton<IEventStoreAdapter>(_ =>
        {
            var adapter = new SqliteEventStoreAdapter(connectionString);
            if (ensureSchema)
            {
                // Bootstrap-only sync wait. App startup blocks once on this — acceptable
                // for a DI factory that runs exactly once per process.
                adapter.EnsureSchemaAsync().AsTask().GetAwaiter().GetResult();
            }
            return adapter;
        });
        builder.Services.TryAddSingleton<IEventStore, EventStore>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqliteEventStoreHealthCheck"/> with the health check system.
    /// Opens a connection and executes <c>SELECT 1</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="connectionString">SQLite connection string.</param>
    /// <param name="name">Health check registration name. Defaults to <c>sqlite-event-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    public static IHealthChecksBuilder AddSqliteEventStore(
        this IHealthChecksBuilder builder,
        string connectionString,
        string name = "sqlite-event-store",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new SqliteEventStoreHealthCheck(connectionString),
            failureStatus,
            tags));
}
