// samples/ZeroAlloc.EventSourcing.Sql.AotSmoke/Program.cs
// The shared SQL stores under PublishAot=true, against PostgreSQL at ZA_ES_AOTSMOKE_POSTGRES and
// SQL Server at ZA_ES_AOTSMOKE_SQLSERVER. For each server, the checkpoint, snapshot, dead-letter
// and projection stores are registered through their EventSourcingBuilder extensions, resolved
// from DI, and each round-trips a value. The PostgreSQL health checks report healthy.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.AotSmoke.Shared;
using ZeroAlloc.EventSourcing.Sql;

try
{
    var postgres = Smoke.RequireEnvironment("ZA_ES_AOTSMOKE_POSTGRES");
    var sqlServer = Smoke.RequireEnvironment("ZA_ES_AOTSMOKE_SQLSERVER");

    await using (var provider = BuildProvider(builder => builder
        .UsePostgreSqlCheckpointStore(postgres)
        .UsePostgreSqlSnapshotStore<PingTotal>(postgres)
        .UsePostgreSqlDeadLetterStore(postgres)
        .UsePostgreSqlProjectionStore(postgres)))
    {
        await ((PostgreSqlCheckpointStore)provider.GetRequiredService<ICheckpointStore>()).EnsureSchemaAsync().ConfigureAwait(false);
        await ((PostgreSqlSnapshotStore<PingTotal>)provider.GetRequiredService<ISnapshotStore<PingTotal>>()).EnsureSchemaAsync().ConfigureAwait(false);
        await ((PostgreSqlDeadLetterStore)provider.GetRequiredService<IDeadLetterStore>()).EnsureSchemaAsync().ConfigureAwait(false);
        await ((PostgreSqlProjectionStore)provider.GetRequiredService<IProjectionStore>()).EnsureSchemaAsync().ConfigureAwait(false);
        await RunStoresAsync(provider, "PostgreSql").ConfigureAwait(false);

        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        await CheckHealthyAsync(new PostgreSqlEventStoreHealthCheck(dataSource), "PostgreSql event store").ConfigureAwait(false);
        await CheckHealthyAsync(new PostgreSqlCheckpointStoreHealthCheck(dataSource), "PostgreSql checkpoint store").ConfigureAwait(false);
    }

    await using (var provider = BuildProvider(builder => builder
        .UseSqlServerCheckpointStore(sqlServer)
        .UseSqlServerSnapshotStore<PingTotal>(sqlServer)
        .UseSqlServerDeadLetterStore(sqlServer)
        .UseSqlServerProjectionStore(sqlServer)))
    {
        await ((SqlServerCheckpointStore)provider.GetRequiredService<ICheckpointStore>()).EnsureSchemaAsync().ConfigureAwait(false);
        await ((SqlServerSnapshotStore<PingTotal>)provider.GetRequiredService<ISnapshotStore<PingTotal>>()).EnsureSchemaAsync().ConfigureAwait(false);
        await ((SqlServerDeadLetterStore)provider.GetRequiredService<IDeadLetterStore>()).EnsureSchemaAsync().ConfigureAwait(false);
        await ((SqlServerProjectionStore)provider.GetRequiredService<IProjectionStore>()).EnsureSchemaAsync().ConfigureAwait(false);
        await RunStoresAsync(provider, "SqlServer").ConfigureAwait(false);
    }
}
catch (SmokeFailure e)
{
    Console.Error.WriteLine($"Sql AOT smoke FAIL: {e.Message}");
    return 1;
}

Console.WriteLine("Sql AOT smoke PASS");
return 0;

static ServiceProvider BuildProvider(Action<EventSourcingBuilder> configure)
{
    var services = new ServiceCollection();
    services.AddSingleton<IEventSerializer, PingSerializer>();
    services.AddSingleton<IEventTypeRegistry, PingRegistry>();
    configure(services.AddEventSourcing());
    return services.BuildServiceProvider();
}

static async Task RunStoresAsync(IServiceProvider provider, string label)
{
    var checkpoints = provider.GetRequiredService<ICheckpointStore>();
    var consumer = Smoke.Unique("consumer");
    await checkpoints.WriteAsync(consumer, new StreamPosition(5)).ConfigureAwait(false);
    var checkpoint = await checkpoints.ReadAsync(consumer).ConfigureAwait(false);
    Smoke.Check(checkpoint?.Value == 5, $"{label}: checkpoint read back {checkpoint?.Value}, expected 5");
    await checkpoints.DeleteAsync(consumer).ConfigureAwait(false);
    checkpoint = await checkpoints.ReadAsync(consumer).ConfigureAwait(false);
    Smoke.Check(checkpoint is null, $"{label}: checkpoint read back {checkpoint?.Value} after delete, expected none");

    var snapshots = provider.GetRequiredService<ISnapshotStore<PingTotal>>();
    var stream = new StreamId(Smoke.Unique("snapshot"));
    await snapshots.WriteAsync(stream, new StreamPosition(3), new PingTotal(6)).ConfigureAwait(false);
    var snapshot = await snapshots.ReadAsync(stream).ConfigureAwait(false);
    Smoke.Check(
        snapshot is { Position.Value: 3, State.Total: 6 },
        $"{label}: snapshot read back {snapshot}, expected PingTotal 6 at position 3");

    var deadLetters = provider.GetRequiredService<IDeadLetterStore>();
    var failed = new EventEnvelope(new StreamId(Smoke.Unique("dead")), new StreamPosition(9), new Ping(9), EventMetadata.New(nameof(Ping)));
    await deadLetters.WriteAsync(consumer, failed, new InvalidOperationException("smoke failure")).ConfigureAwait(false);
    DeadLetterEntry? entry = null;
    await foreach (var candidate in deadLetters.ReadAllAsync().ConfigureAwait(false))
    {
        if (string.Equals(candidate.ConsumerId, consumer, StringComparison.Ordinal))
            entry = candidate;
    }
    Smoke.Check(
        entry is not null
            && entry.Envelope.Event is Ping { Sequence: 9 }
            && entry.Envelope.StreamId == failed.StreamId
            && entry.Envelope.Position.Value == 9
            && string.Equals(entry.ExceptionMessage, "smoke failure", StringComparison.Ordinal),
        $"{label}: dead letter read back {entry?.ToString() ?? "nothing"}, expected Ping 9 failed with 'smoke failure'");

    var projections = provider.GetRequiredService<IProjectionStore>();
    var key = Smoke.Unique("projection");
    await projections.SaveAsync(key, "{\"total\":6}").ConfigureAwait(false);
    var state = await projections.LoadAsync(key).ConfigureAwait(false);
    Smoke.Check(string.Equals(state, "{\"total\":6}", StringComparison.Ordinal), $"{label}: projection read back {state}");
    await projections.ClearAsync(key).ConfigureAwait(false);
    state = await projections.LoadAsync(key).ConfigureAwait(false);
    Smoke.Check(state is null, $"{label}: projection read back {state} after clear, expected none");
}

static async Task CheckHealthyAsync(IHealthCheck check, string label)
{
    var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
    Smoke.Check(
        result.Status == HealthStatus.Healthy,
        $"{label}: health check reported {result.Status}: {result.Description} {result.Exception?.Message}");
}
