using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
}
