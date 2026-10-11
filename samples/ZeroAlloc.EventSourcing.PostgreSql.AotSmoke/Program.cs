// samples/ZeroAlloc.EventSourcing.PostgreSql.AotSmoke/Program.cs
// The PostgreSQL adapter under PublishAot=true, against the server named by
// ZA_ES_AOTSMOKE_POSTGRES, a Npgsql connection string. Validates:
//   - PostgreSqlEventStoreAdapter bootstraps its schema
//   - an EventStore over it appends, reads back, rejects a stale append and
//     delivers through a subscription
//   - UsePostgreSqlEventStore resolves an IEventStore from DI that reads the same stream
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.AotSmoke.Shared;
using ZeroAlloc.EventSourcing.PostgreSql;

try
{
    var connectionString = Smoke.RequireEnvironment("ZA_ES_AOTSMOKE_POSTGRES");

    await using (var dataSource = NpgsqlDataSource.Create(connectionString))
    {
        var adapter = new PostgreSqlEventStoreAdapter(dataSource);
        await adapter.EnsureSchemaAsync().ConfigureAwait(false);
        await EventStoreSmoke.RunAsync(adapter, "PostgreSql").ConfigureAwait(false);
    }

    var services = new ServiceCollection();
    services.AddSingleton<IEventSerializer, PingSerializer>();
    services.AddSingleton<IEventTypeRegistry, PingRegistry>();
    services.AddEventSourcing().UsePostgreSqlEventStore(connectionString);
    await using var provider = services.BuildServiceProvider();

    var store = provider.GetRequiredService<IEventStore>();
    var stream = new StreamId(Smoke.Unique("smoke-di"));
    var appended = await store.AppendAsync(stream, new object[] { new Ping(7) }.AsMemory(), StreamPosition.Start)
        .ConfigureAwait(false);
    Smoke.Check(appended.IsSuccess, "PostgreSql: append through the DI-registered IEventStore failed");

    var read = new List<object>();
    await foreach (var envelope in store.ReadAsync(stream, StreamPosition.Start).ConfigureAwait(false))
        read.Add(envelope.Event);
    Smoke.Check(
        read.Count == 1 && read[0] is Ping { Sequence: 7 },
        $"PostgreSql: the DI-registered IEventStore read back {string.Join(", ", read)}, expected Ping 7");
}
catch (SmokeFailure e)
{
    Console.Error.WriteLine($"PostgreSql AOT smoke FAIL: {e.Message}");
    return 1;
}

Console.WriteLine("PostgreSql AOT smoke PASS");
return 0;
