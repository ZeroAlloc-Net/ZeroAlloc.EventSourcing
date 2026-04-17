using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// <see cref="EventSourcingBuilder"/> extension methods for registering shared SQL
/// checkpoint, snapshot, dead-letter, and projection stores (PostgreSQL and SQL Server).
/// </summary>
public static class EventSourcingBuilderExtensions
{
    // ── PostgreSQL ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <see cref="PostgreSqlCheckpointStore"/> as <see cref="ICheckpointStore"/>.
    /// </summary>
    /// <remarks>
    /// Also registers a <see cref="NpgsqlDataSource"/> singleton using
    /// <see cref="NpgsqlDataSource.Create(string)"/> if one is not already present.
    /// Uses <c>TryAddSingleton</c> — existing registrations are not overwritten.
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UsePostgreSqlCheckpointStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.TryAddSingleton<ICheckpointStore, PostgreSqlCheckpointStore>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="PostgreSqlSnapshotStore{TState}"/> as the open-generic
    /// <see cref="ISnapshotStore{TState}"/> implementation.
    /// </summary>
    /// <remarks>
    /// Also registers a <see cref="NpgsqlDataSource"/> singleton using
    /// <see cref="NpgsqlDataSource.Create(string)"/> if one is not already present.
    /// Uses <c>TryAdd</c> — an existing open-generic registration is not overwritten.
    /// <para>
    /// Because <see cref="PostgreSqlSnapshotStore{TState}"/> accepts an optional
    /// <see cref="IEventSerializer"/>, a registered <see cref="IEventSerializer"/> will
    /// be injected automatically; if none is registered the store operates without
    /// serialization support.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UsePostgreSqlSnapshotStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.TryAdd(
            ServiceDescriptor.Singleton(
                typeof(ISnapshotStore<>),
                typeof(PostgreSqlSnapshotStore<>)));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="PostgreSqlDeadLetterStore"/> as <see cref="IDeadLetterStore"/>.
    /// </summary>
    /// <remarks>
    /// Also registers a <see cref="NpgsqlDataSource"/> singleton using
    /// <see cref="NpgsqlDataSource.Create(string)"/> if one is not already present.
    /// Uses <c>TryAddSingleton</c> — existing registrations are not overwritten.
    /// <para>
    /// Requires <see cref="IEventSerializer"/> to be registered in the container.
    /// <see cref="ServiceCollectionExtensions.AddEventSourcing"/> registers the default
    /// <c>ZeroAllocEventSerializer</c> automatically.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UsePostgreSqlDeadLetterStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.TryAddSingleton<IDeadLetterStore, PostgreSqlDeadLetterStore>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="PostgreSqlProjectionStore"/> as <see cref="IProjectionStore"/>.
    /// </summary>
    /// <remarks>
    /// Also registers a <see cref="NpgsqlDataSource"/> singleton using
    /// <see cref="NpgsqlDataSource.Create(string)"/> if one is not already present.
    /// Uses <c>TryAddSingleton</c> — existing registrations are not overwritten.
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UsePostgreSqlProjectionStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.TryAddSingleton<IProjectionStore, PostgreSqlProjectionStore>();
        return builder;
    }

    // ── SQL Server ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <see cref="SqlServerCheckpointStore"/> as <see cref="ICheckpointStore"/>.
    /// </summary>
    /// <remarks>
    /// Uses <c>TryAddSingleton</c> — an existing <see cref="ICheckpointStore"/> registration
    /// is not overwritten.
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UseSqlServerCheckpointStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton<ICheckpointStore>(
            _ => new SqlServerCheckpointStore(connectionString));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqlServerSnapshotStore{TState}"/> as
    /// <see cref="ISnapshotStore{TState}"/> for the specified aggregate state type.
    /// </summary>
    /// <remarks>
    /// SQL Server snapshot stores cannot be registered as an open generic because the connection
    /// string is not DI-resolvable. Call this method once per aggregate state type.
    /// Uses <c>TryAddSingleton</c> — an existing <see cref="ISnapshotStore{TState}"/>
    /// registration is not overwritten.
    /// <para>
    /// If <see cref="IEventSerializer"/> is registered in the container it will be used for
    /// serialization; otherwise the store operates without serialization support.
    /// </para>
    /// </remarks>
    /// <typeparam name="TState">The aggregate state type.</typeparam>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UseSqlServerSnapshotStore<TState>(
        this EventSourcingBuilder builder, string connectionString)
        where TState : struct
    {
        builder.Services.TryAddSingleton<ISnapshotStore<TState>>(
            sp => new SqlServerSnapshotStore<TState>(
                connectionString,
                sp.GetService<IEventSerializer>()));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqlServerDeadLetterStore"/> as <see cref="IDeadLetterStore"/>.
    /// </summary>
    /// <remarks>
    /// Uses <c>TryAddSingleton</c> — an existing <see cref="IDeadLetterStore"/> registration
    /// is not overwritten.
    /// <para>
    /// Requires <see cref="IEventSerializer"/> to be registered in the container.
    /// <see cref="ServiceCollectionExtensions.AddEventSourcing"/> registers the default
    /// <c>ZeroAllocEventSerializer</c> automatically.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UseSqlServerDeadLetterStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton<IDeadLetterStore>(
            sp => new SqlServerDeadLetterStore(
                connectionString,
                sp.GetRequiredService<IEventSerializer>()));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SqlServerProjectionStore"/> as <see cref="IProjectionStore"/>.
    /// </summary>
    /// <remarks>
    /// Uses <c>TryAddSingleton</c> — an existing <see cref="IProjectionStore"/> registration
    /// is not overwritten.
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid SQL Server connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UseSqlServerProjectionStore(
        this EventSourcingBuilder builder, string connectionString)
    {
        builder.Services.TryAddSingleton<IProjectionStore>(
            _ => new SqlServerProjectionStore(connectionString));
        return builder;
    }
}
