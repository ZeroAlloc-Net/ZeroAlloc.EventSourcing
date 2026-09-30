using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.SqlServer;
using ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.SqlAdaptersDocs;
using DocsJsonEventSerializer = ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.SqlAdaptersDocs.JsonEventSerializer;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

/// <summary>
/// Runs the snippets of docs/usage-guides/sql-adapters.md that need no database; they are held in
/// <c>SqlAdaptersDocSnippets.cs</c>. See issue #410.
/// </summary>
public sealed class SqlAdaptersDocTests
{
    private static IEventStore NewEventStore(IEventStoreAdapter? adapter = null)
        => new EventStore(adapter ?? new InMemoryEventStoreAdapter(), new DocsJsonEventSerializer(), new OrderEventTypeRegistry());

    [Fact]
    public void Serializers_RoundTrip()
    {
        var json = new DocsJsonEventSerializer();
        var compressed = new CompressedEventSerializer(json);
        object @event = new OrderPlaced("alice");

        json.Deserialize(json.Serialize(@event), typeof(OrderPlaced)).Should().Be(@event);
        compressed.Deserialize(compressed.Serialize(@event), typeof(OrderPlaced)).Should().Be(@event);
    }

    [Fact]
    public async Task Archiver_CopiesTheStream()
    {
        var live = NewEventStore();
        var archive = NewEventStore();
        var stream = new StreamId("order-1");
        await live.AppendAsync(stream, new object[] { new OrderPlaced("alice"), new ItemAdded(2m) }, StreamPosition.Start);

        var archiver = new EventArchiver(live, archive);
        await archiver.ArchiveStreamAsync(stream);

        var copied = new List<object>();
        await foreach (var envelope in archive.ReadAsync(stream))
            copied.Add(envelope.Event);
        copied.Should().Equal(new OrderPlaced("alice"), new ItemAdded(2m));

        // A second run conflicts instead of copying the events twice
        await archiver.Invoking(a => a.ArchiveStreamAsync(stream)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Migration_RegistersTheUpcaster()
    {
        var services = new ServiceCollection();
        Migration.Register(services, "Server=unused");

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IUpcasterPipeline>();

        pipeline.TryUpcast(new OrderPlacedEvent_V1("1", 5m), out var upgraded).Should().BeTrue();
        upgraded.Should().Be(new OrderPlacedEvent_V2("1", 5m, "unknown"));
    }

    [Fact]
    public async Task LoggingAdapter_PassesThrough()
    {
        var store = NewEventStore(new LoggingEventStoreAdapter(new InMemoryEventStoreAdapter(), NullLogger<LoggingEventStoreAdapter>.Instance));
        var stream = new StreamId("order-1");

        (await store.AppendAsync(stream, new object[] { new OrderPlaced("alice") }, StreamPosition.Start)).IsSuccess.Should().BeTrue();

        var count = 0;
        await foreach (var _ in store.ReadAsync(stream))
            count++;
        count.Should().Be(1);
    }

    [Fact]
    public void DiConfiguration_ResolvesTheConfiguredAdapter()
    {
        var services = new ServiceCollection();
        Registration.Register(services, DatabaseType.SqlServer, "Server=localhost;Database=EventStore;TrustServerCertificate=true");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEventStoreAdapter>().Should().BeOfType<SqlServerEventStoreAdapter>();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<EventStore>();
    }

    [Fact]
    public void HealthChecks_AreRegistered()
    {
        var services = new ServiceCollection();
        Monitoring.Register(services, Npgsql.NpgsqlDataSource.Create("Host=localhost"), "Server=localhost");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>();

        options.Value.Registrations.Select(r => r.Name).Should().Contain(["postgresql-event-store", "sqlserver-event-store"]);
    }
}
