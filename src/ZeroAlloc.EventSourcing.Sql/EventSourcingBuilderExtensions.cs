using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    /// Registers <see cref="PostgreSqlSnapshotStore{TState}"/> as
    /// <see cref="ISnapshotStore{TState}"/> for the specified aggregate state type.
    /// </summary>
    /// <remarks>
    /// Call this method once per aggregate state type. The registration is closed over
    /// <typeparamref name="TState"/>, so it resolves under NativeAOT; an open-generic
    /// registration cannot, because every snapshot state is a value type.
    /// Also registers a <see cref="NpgsqlDataSource"/> singleton using
    /// <see cref="NpgsqlDataSource.Create(string)"/> if one is not already present.
    /// Uses <c>TryAddSingleton</c> — an existing <see cref="ISnapshotStore{TState}"/>
    /// registration is not overwritten.
    /// <para>
    /// If <see cref="IEventSerializer"/> is registered in the container it will be used for
    /// serialization; otherwise the store operates without serialization support.
    /// </para>
    /// </remarks>
    /// <typeparam name="TState">The aggregate state type.</typeparam>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static EventSourcingBuilder UsePostgreSqlSnapshotStore<TState>(
        this EventSourcingBuilder builder, string connectionString)
        where TState : struct
    {
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.TryAddSingleton<ISnapshotStore<TState>>(
            static sp => new PostgreSqlSnapshotStore<TState>(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<IEventSerializer>()));
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
    /// <para>
    /// Obsolete: the open-generic registration throws under NativeAOT for every snapshot state,
    /// because <see cref="ISnapshotStore{TState}"/> requires a value type and the container
    /// cannot build a value-type instantiation of an open generic without dynamic code.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="EventSourcingBuilder"/> to configure.</param>
    /// <param name="connectionString">A valid PostgreSQL connection string.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    [Obsolete("Use UsePostgreSqlSnapshotStore<TState>(connectionString) once per aggregate state type. "
        + "The open-generic registration cannot resolve a value-type state under NativeAOT. "
        + "This overload will be removed in the next major version.", DiagnosticId = "ZAES003")]
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

    // ── Health Checks ─────────────────────────────────────────────────────────

    private const string EventStoreHealthCheckName = "postgresql-event-store";
    private const string CheckpointStoreHealthCheckName = "postgresql-checkpoint-store";

    private const string ObsoleteHealthCheckOverload =
        "Use the overload that takes only the connection string or data source, or the overload "
        + "taking Action<PostgreSqlHealthCheckOptions> to set the name, failure status or tags. "
        + "This overload will be removed in the next major version.";

    /// <summary>
    /// Registers <see cref="PostgreSqlEventStoreHealthCheck"/> under the name
    /// <c>postgresql-event-store</c>. Performs a <c>SELECT 1</c> on a pooled
    /// connection for the connection string.
    /// </summary>
    /// <remarks>
    /// Each health check invocation opens a connection from Npgsql's shared pool for
    /// <paramref name="connectionString"/> and returns it afterwards. No
    /// <see cref="NpgsqlDataSource"/> is registered or shared, so calling this method with
    /// different connection strings works correctly.
    /// To set the name, failure status or tags, use
    /// <see cref="AddPostgreSqlEventStore(IHealthChecksBuilder, Action{PostgreSqlHealthCheckOptions})"/>.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="connectionString">PostgreSQL connection string used to create the data source.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static IHealthChecksBuilder AddPostgreSqlEventStore(
        this IHealthChecksBuilder builder,
        string connectionString)
        => AddEventStoreCheck(builder, connectionString, EventStoreHealthCheckName, failureStatus: null, tags: null);

    /// <summary>
    /// Registers <see cref="PostgreSqlEventStoreHealthCheck"/> under the name
    /// <c>postgresql-event-store</c>, using an existing <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// Use this overload when you already manage a <see cref="NpgsqlDataSource"/> externally
    /// and want to share it with the health check.
    /// To set the name, failure status or tags, use
    /// <see cref="AddPostgreSqlEventStore(IHealthChecksBuilder, Action{PostgreSqlHealthCheckOptions})"/>.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="dataSource">An existing <see cref="NpgsqlDataSource"/> to use.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static IHealthChecksBuilder AddPostgreSqlEventStore(
        this IHealthChecksBuilder builder,
        NpgsqlDataSource dataSource)
        => AddEventStoreCheck(builder, dataSource, EventStoreHealthCheckName, failureStatus: null, tags: null);

    /// <summary>
    /// Registers <see cref="PostgreSqlEventStoreHealthCheck"/> as configured by
    /// <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// Set exactly one of <see cref="PostgreSqlHealthCheckOptions.ConnectionString"/> and
    /// <see cref="PostgreSqlHealthCheckOptions.DataSource"/>. The name defaults to
    /// <c>postgresql-event-store</c>. <paramref name="configure"/> runs once, during this call.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="configure">Sets the connection, and optionally the name, failure status and tags.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Neither or both of the connection string and the data source were set.
    /// </exception>
    public static IHealthChecksBuilder AddPostgreSqlEventStore(
        this IHealthChecksBuilder builder,
        Action<PostgreSqlHealthCheckOptions> configure)
    {
        var options = Configure(configure);
        var name = options.Name ?? EventStoreHealthCheckName;
        return options.DataSource is { } dataSource
            ? AddEventStoreCheck(builder, dataSource, name, options.FailureStatus, options.Tags)
            : AddEventStoreCheck(builder, options.ConnectionString!, name, options.FailureStatus, options.Tags);
    }

    /// <summary>
    /// Registers <see cref="PostgreSqlEventStoreHealthCheck"/> with the health check system.
    /// Performs a <c>SELECT 1</c> on a pooled connection for the connection string.
    /// </summary>
    /// <remarks>
    /// Obsolete: this overload and its <see cref="NpgsqlDataSource"/> sibling both carry optional
    /// parameters, which violates RS0026.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="connectionString">PostgreSQL connection string used to create the data source.</param>
    /// <param name="name">Health check registration name. Defaults to <c>postgresql-event-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    [Obsolete(ObsoleteHealthCheckOverload, DiagnosticId = "ZAES002")]
    public static IHealthChecksBuilder AddPostgreSqlEventStore(
        this IHealthChecksBuilder builder,
        string connectionString,
        string name = EventStoreHealthCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => AddEventStoreCheck(builder, connectionString, name, failureStatus, tags);

    /// <summary>
    /// Registers <see cref="PostgreSqlEventStoreHealthCheck"/> using an existing <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// Obsolete: this overload and its connection-string sibling both carry optional
    /// parameters, which violates RS0026.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="dataSource">An existing <see cref="NpgsqlDataSource"/> to use.</param>
    /// <param name="name">Health check registration name. Defaults to <c>postgresql-event-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    [Obsolete(ObsoleteHealthCheckOverload, DiagnosticId = "ZAES002")]
    public static IHealthChecksBuilder AddPostgreSqlEventStore(
        this IHealthChecksBuilder builder,
        NpgsqlDataSource dataSource,
        string name = EventStoreHealthCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => AddEventStoreCheck(builder, dataSource, name, failureStatus, tags);

    /// <summary>
    /// Registers <see cref="PostgreSqlCheckpointStoreHealthCheck"/> under the name
    /// <c>postgresql-checkpoint-store</c>. Performs a <c>SELECT 1</c> on a pooled
    /// connection for the connection string.
    /// </summary>
    /// <remarks>
    /// Each health check invocation opens a connection from Npgsql's shared pool for
    /// <paramref name="connectionString"/> and returns it afterwards. No
    /// <see cref="NpgsqlDataSource"/> is registered or shared, so calling this method with
    /// different connection strings works correctly.
    /// To set the name, failure status or tags, use
    /// <see cref="AddPostgreSqlCheckpointStore(IHealthChecksBuilder, Action{PostgreSqlHealthCheckOptions})"/>.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="connectionString">PostgreSQL connection string used to create the data source.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static IHealthChecksBuilder AddPostgreSqlCheckpointStore(
        this IHealthChecksBuilder builder,
        string connectionString)
        => AddCheckpointStoreCheck(builder, connectionString, CheckpointStoreHealthCheckName, failureStatus: null, tags: null);

    /// <summary>
    /// Registers <see cref="PostgreSqlCheckpointStoreHealthCheck"/> under the name
    /// <c>postgresql-checkpoint-store</c>, using an existing <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// Use this overload when you already manage a <see cref="NpgsqlDataSource"/> externally
    /// and want to share it with the health check.
    /// To set the name, failure status or tags, use
    /// <see cref="AddPostgreSqlCheckpointStore(IHealthChecksBuilder, Action{PostgreSqlHealthCheckOptions})"/>.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="dataSource">An existing <see cref="NpgsqlDataSource"/> to use.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    public static IHealthChecksBuilder AddPostgreSqlCheckpointStore(
        this IHealthChecksBuilder builder,
        NpgsqlDataSource dataSource)
        => AddCheckpointStoreCheck(builder, dataSource, CheckpointStoreHealthCheckName, failureStatus: null, tags: null);

    /// <summary>
    /// Registers <see cref="PostgreSqlCheckpointStoreHealthCheck"/> as configured by
    /// <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// Set exactly one of <see cref="PostgreSqlHealthCheckOptions.ConnectionString"/> and
    /// <see cref="PostgreSqlHealthCheckOptions.DataSource"/>. The name defaults to
    /// <c>postgresql-checkpoint-store</c>. <paramref name="configure"/> runs once, during this call.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="configure">Sets the connection, and optionally the name, failure status and tags.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Neither or both of the connection string and the data source were set.
    /// </exception>
    public static IHealthChecksBuilder AddPostgreSqlCheckpointStore(
        this IHealthChecksBuilder builder,
        Action<PostgreSqlHealthCheckOptions> configure)
    {
        var options = Configure(configure);
        var name = options.Name ?? CheckpointStoreHealthCheckName;
        return options.DataSource is { } dataSource
            ? AddCheckpointStoreCheck(builder, dataSource, name, options.FailureStatus, options.Tags)
            : AddCheckpointStoreCheck(builder, options.ConnectionString!, name, options.FailureStatus, options.Tags);
    }

    /// <summary>
    /// Registers <see cref="PostgreSqlCheckpointStoreHealthCheck"/> with the health check system.
    /// Performs a <c>SELECT 1</c> on a pooled connection for the connection string.
    /// </summary>
    /// <remarks>
    /// Obsolete: this overload and its <see cref="NpgsqlDataSource"/> sibling both carry optional
    /// parameters, which violates RS0026.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="connectionString">PostgreSQL connection string used to create the data source.</param>
    /// <param name="name">Health check registration name. Defaults to <c>postgresql-checkpoint-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    [Obsolete(ObsoleteHealthCheckOverload, DiagnosticId = "ZAES002")]
    public static IHealthChecksBuilder AddPostgreSqlCheckpointStore(
        this IHealthChecksBuilder builder,
        string connectionString,
        string name = CheckpointStoreHealthCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => AddCheckpointStoreCheck(builder, connectionString, name, failureStatus, tags);

    /// <summary>
    /// Registers <see cref="PostgreSqlCheckpointStoreHealthCheck"/> using an existing <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// Obsolete: this overload and its connection-string sibling both carry optional
    /// parameters, which violates RS0026.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="dataSource">An existing <see cref="NpgsqlDataSource"/> to use.</param>
    /// <param name="name">Health check registration name. Defaults to <c>postgresql-checkpoint-store</c>.</param>
    /// <param name="failureStatus">Status to report on failure. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags for filtering.</param>
    /// <returns>The same <paramref name="builder"/> for method chaining.</returns>
    [Obsolete(ObsoleteHealthCheckOverload, DiagnosticId = "ZAES002")]
    public static IHealthChecksBuilder AddPostgreSqlCheckpointStore(
        this IHealthChecksBuilder builder,
        NpgsqlDataSource dataSource,
        string name = CheckpointStoreHealthCheckName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        => AddCheckpointStoreCheck(builder, dataSource, name, failureStatus, tags);

    private static PostgreSqlHealthCheckOptions Configure(Action<PostgreSqlHealthCheckOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new PostgreSqlHealthCheckOptions();
        configure(options);
        if ((options.ConnectionString is null) == (options.DataSource is null))
        {
            throw new ArgumentException(
                "Set exactly one of PostgreSqlHealthCheckOptions.ConnectionString and "
                + "PostgreSqlHealthCheckOptions.DataSource.",
                nameof(configure));
        }

        return options;
    }

    private static IHealthChecksBuilder AddEventStoreCheck(
        IHealthChecksBuilder builder, string connectionString, string name,
        HealthStatus? failureStatus, IEnumerable<string>? tags)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new PostgreSqlEventStoreHealthCheck(connectionString),
            failureStatus,
            tags));

    private static IHealthChecksBuilder AddEventStoreCheck(
        IHealthChecksBuilder builder, NpgsqlDataSource dataSource, string name,
        HealthStatus? failureStatus, IEnumerable<string>? tags)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new PostgreSqlEventStoreHealthCheck(dataSource),
            failureStatus,
            tags));

    private static IHealthChecksBuilder AddCheckpointStoreCheck(
        IHealthChecksBuilder builder, string connectionString, string name,
        HealthStatus? failureStatus, IEnumerable<string>? tags)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new PostgreSqlCheckpointStoreHealthCheck(connectionString),
            failureStatus,
            tags));

    private static IHealthChecksBuilder AddCheckpointStoreCheck(
        IHealthChecksBuilder builder, NpgsqlDataSource dataSource, string name,
        HealthStatus? failureStatus, IEnumerable<string>? tags)
        => builder.Add(new HealthCheckRegistration(
            name,
            _ => new PostgreSqlCheckpointStoreHealthCheck(dataSource),
            failureStatus,
            tags));
}
