using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ZeroAlloc.EventSourcing.PostgreSql;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extensions for the PostgreSQL event store adapter.
/// </summary>
public static class EventSourcingBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgreSqlEventStoreAdapter"/> as <see cref="IEventStoreAdapter"/>
    /// and <see cref="EventStore"/> as <see cref="IEventStore"/>.
    /// </summary>
    /// <param name="builder">The event sourcing builder.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <remarks>
    /// Also registers a singleton <see cref="NpgsqlDataSource"/> built from
    /// <paramref name="connectionString"/> using <c>TryAddSingleton</c>. If a
    /// <see cref="NpgsqlDataSource"/> is already registered it will not be replaced.
    /// <para>
    /// <see cref="IEventTypeRegistry"/> must be registered separately.
    /// </para>
    /// </remarks>
    public static EventSourcingBuilder UsePostgreSqlEventStore(
        this EventSourcingBuilder builder,
        string connectionString)
    {
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.TryAddSingleton<IEventStoreAdapter, PostgreSqlEventStoreAdapter>();
        builder.Services.TryAddSingleton<IEventStore, EventStore>();
        return builder;
    }
}
