// samples/ZeroAlloc.EventSourcing.SqlServer.AotSmoke/Program.cs
// The SQL Server adapter under PublishAot=true, against the server named by
// ZA_ES_AOTSMOKE_SQLSERVER, a Microsoft.Data.SqlClient connection string. Validates:
//   - SqlServerEventStoreAdapter bootstraps its schema
//   - an EventStore over it appends, reads back, rejects a stale append and
//     delivers through a subscription
//   - UseSqlServerEventStore resolves an IEventStore from DI that reads the same stream
//   - SqlServerEventStoreHealthCheck reports the server healthy
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.AotSmoke.Shared;
using ZeroAlloc.EventSourcing.SqlServer;

try
{
    var connectionString = Smoke.RequireEnvironment("ZA_ES_AOTSMOKE_SQLSERVER");

    var adapter = new SqlServerEventStoreAdapter(connectionString);
    await adapter.EnsureSchemaAsync().ConfigureAwait(false);
    await EventStoreSmoke.RunAsync(adapter, "SqlServer").ConfigureAwait(false);

    var services = new ServiceCollection();
    services.AddSingleton<IEventSerializer, PingSerializer>();
    services.AddSingleton<IEventTypeRegistry, PingRegistry>();
    services.AddEventSourcing().UseSqlServerEventStore(connectionString);
    await using var provider = services.BuildServiceProvider();

    var store = provider.GetRequiredService<IEventStore>();
    var stream = new StreamId(Smoke.Unique("smoke-di"));
    var appended = await store.AppendAsync(stream, new object[] { new Ping(7) }.AsMemory(), StreamPosition.Start)
        .ConfigureAwait(false);
    Smoke.Check(appended.IsSuccess, "SqlServer: append through the DI-registered IEventStore failed");

    var read = new List<object>();
    await foreach (var envelope in store.ReadAsync(stream, StreamPosition.Start).ConfigureAwait(false))
        read.Add(envelope.Event);
    Smoke.Check(
        read.Count == 1 && read[0] is Ping { Sequence: 7 },
        $"SqlServer: the DI-registered IEventStore read back {string.Join(", ", read)}, expected Ping 7");

    var health = await new SqlServerEventStoreHealthCheck(connectionString)
        .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
    Smoke.Check(
        health.Status == HealthStatus.Healthy,
        $"SqlServer: the health check reported {health.Status}: {health.Description} {health.Exception?.Message}");
}
catch (SmokeFailure e)
{
    Console.Error.WriteLine($"SqlServer AOT smoke FAIL: {e.Message}");
    return 1;
}

Console.WriteLine("SqlServer AOT smoke PASS");
return 0;
