using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.PostgreSql;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.SqlServer;
using ZeroAlloc.Results;

// The C# in docs/usage-guides/sql-adapters.md, copied as it appears there between
// "--- snippet ---" markers and compiled against the public API. The parts that need a database
// are only compiled; SqlAdaptersDocTests runs the rest. The Testcontainers examples live in the
// PostgreSql and SqlServer test projects and run there. When a snippet changes in the docs,
// change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.SqlAdaptersDocs;

public static class Setup
{
    public static async Task<IEventStore> PostgreSqlAsync(IEventSerializer serializer)
    {
        // --- snippet: "PostgreSQL Basic Configuration" ---
        var connectionString = "Host=localhost;Database=EventStore;Username=postgres;Password=password";

        // The adapter takes an NpgsqlDataSource, which owns the connection pool: create one per
        // database and keep it for the lifetime of the application
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var adapter = new PostgreSqlEventStoreAdapter(dataSource);

        // Create the event_store table if it does not exist; safe to run on every start
        await adapter.EnsureSchemaAsync();

        // Create event store
        var registry = new OrderEventTypeRegistry();  // generated for your Order aggregate
        var eventStore = new EventStore(adapter, serializer, registry);
        // --- end snippet ---
        return eventStore;
    }

    public static PostgreSqlEventStoreAdapter PostgreSqlPooling()
    {
        // --- snippet: "PostgreSQL Connection Pooling" ---
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "EventStore",
            Username = "postgres",
            Password = "password",

            // Connection pooling
            MaxPoolSize = 100,           // Max connections
            MinPoolSize = 10,            // Min connections
            CommandTimeout = 30,         // Command timeout (seconds)
            ConnectionIdleLifetime = 300 // Idle connection timeout (seconds)
        }.ConnectionString;

        var adapter = new PostgreSqlEventStoreAdapter(NpgsqlDataSource.Create(connectionString));
        // --- end snippet ---
        return adapter;
    }

    public static async Task<IEventStore> SqlServerAsync(IEventSerializer serializer)
    {
        // --- snippet: "SQL Server Basic Configuration" ---
        var connectionString = "Server=localhost;Database=EventStore;User Id=sa;Password=YourPassword123;TrustServerCertificate=true";

        // Create adapter
        var adapter = new SqlServerEventStoreAdapter(connectionString);

        // Create the dbo.event_store table if it does not exist; safe to run on every start
        await adapter.EnsureSchemaAsync();

        // Create event store
        var registry = new OrderEventTypeRegistry();  // generated for your Order aggregate
        var eventStore = new EventStore(adapter, serializer, registry);
        // --- end snippet ---
        return eventStore;
    }

    public static SqlServerEventStoreAdapter SqlServerPooling()
    {
        // --- snippet: "SQL Server Connection Pooling" ---
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = "EventStore",
            UserID = "sa",
            Password = "YourPassword123",

            // Connection pooling
            Pooling = true,
            MaxPoolSize = 100,
            MinPoolSize = 10,
            ConnectTimeout = 30,       // Connection timeout (seconds)
            LoadBalanceTimeout = 300   // Connection lifetime in the pool (seconds)
        }.ConnectionString;

        var adapter = new SqlServerEventStoreAdapter(connectionString);
        // --- end snippet ---
        return adapter;
    }
}

// --- snippet: "JSON Serialization" ---
public class JsonEventSerializer : IEventSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        return JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), Options);
    }

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        return JsonSerializer.Deserialize(payload.Span, eventType, Options)
            ?? throw new InvalidOperationException("Deserialization failed");
    }
}
// --- end snippet ---

// --- snippet: "Compression" ---
public class CompressedEventSerializer : IEventSerializer
{
    private readonly IEventSerializer _inner;

    public CompressedEventSerializer(IEventSerializer inner)
    {
        _inner = inner;
    }

    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        var uncompressed = _inner.Serialize(@event);

        using var target = new MemoryStream();
        using (var gzip = new GZipStream(target, CompressionMode.Compress))
        {
            gzip.Write(uncompressed.Span);
        }

        return target.ToArray();
    }

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        using var source = new MemoryStream(payload.ToArray());
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();

        gzip.CopyTo(target);
        return _inner.Deserialize(target.ToArray(), eventType);
    }
}
// --- end snippet ---

// --- snippet: "Archiving Streams" ---
public class EventArchiver
{
    private readonly IEventStore _live;
    private readonly IEventStore _archive;

    public EventArchiver(IEventStore live, IEventStore archive)
    {
        _live = live;
        _archive = archive;
    }

    public async Task ArchiveStreamAsync(StreamId streamId, CancellationToken ct = default)
    {
        // Copy the stream's events to the archive
        var events = new List<object>();
        await foreach (var envelope in _live.ReadAsync(streamId, StreamPosition.Start, ct))
        {
            events.Add(envelope.Event);
        }

        // The archive gives the events new metadata: event IDs and timestamps are not copied
        var result = await _archive.AppendAsync(streamId, events.ToArray(), StreamPosition.Start, ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Error.ToString());  // e.g. [CONFLICT]: already archived
    }
}
// --- end snippet ---

// --- snippet: "Event Migration" events ---
// The old shape stays readable: keep it, and its entry in the event type registry
public record OrderPlacedEvent_V1(string OrderId, decimal Total);
public record OrderPlacedEvent_V2(string OrderId, decimal Total, string CustomerId);
// --- end snippet ---

public static class Migration
{
    public static void Register(IServiceCollection services, string connectionString)
    {
        // --- snippet: "Event Migration" ---
        services
            .AddEventSourcing()
            .UseSqlServerEventStore(connectionString)
            // Every V1 event read from the store is handed to your code as V2
            .AddUpcaster<OrderPlacedEvent_V1, OrderPlacedEvent_V2>(
                old => new OrderPlacedEvent_V2(old.OrderId, old.Total, CustomerId: "unknown"));
        // --- end snippet ---
    }
}

// --- snippet: "Query Logging" ---
public class LoggingEventStoreAdapter : IEventStoreAdapter
{
    private readonly IEventStoreAdapter _inner;
    private readonly ILogger<LoggingEventStoreAdapter> _logger;

    public LoggingEventStoreAdapter(IEventStoreAdapter inner, ILogger<LoggingEventStoreAdapter> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id,
        ReadOnlyMemory<RawEvent> events,
        StreamPosition expectedVersion,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _inner.AppendAsync(id, events, expectedVersion, ct);
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > 100)
            {
                _logger.LogWarning(
                    "Slow append: {StreamId} took {Ms}ms",
                    id.Value,
                    stopwatch.ElapsedMilliseconds);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Append failed for {StreamId}", id.Value);
            throw;
        }
    }

    public IAsyncEnumerable<RawEvent> ReadAsync(StreamId id, StreamPosition from, CancellationToken ct = default)
        => _inner.ReadAsync(id, from, ct);

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id,
        StreamPosition from,
        Func<RawEvent, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => _inner.SubscribeAsync(id, from, handler, ct);
}
// --- end snippet ---

public static class Monitoring
{
    public static void Register(IServiceCollection services, NpgsqlDataSource dataSource, string sqlServerConnectionString)
    {
        // --- snippet: "Monitoring Metrics" ---
        services.AddHealthChecks()
            .AddPostgreSqlEventStore(dataSource)               // ZeroAlloc.EventSourcing.Sql
            .AddSqlServerEventStore(sqlServerConnectionString); // ZeroAlloc.EventSourcing.SqlServer
        // --- end snippet ---
    }
}

// --- snippet: "Factory Pattern" ---
public interface IEventStoreAdapterFactory
{
    IEventStoreAdapter CreateAdapter(DatabaseType dbType, string connectionString);
}

public class EventStoreAdapterFactory : IEventStoreAdapterFactory
{
    public IEventStoreAdapter CreateAdapter(DatabaseType dbType, string connectionString)
    {
        return dbType switch
        {
            DatabaseType.PostgreSql => new PostgreSqlEventStoreAdapter(NpgsqlDataSource.Create(connectionString)),
            DatabaseType.SqlServer => new SqlServerEventStoreAdapter(connectionString),
            _ => throw new ArgumentException($"Unknown database type: {dbType}")
        };
    }
}

public enum DatabaseType
{
    PostgreSql,
    SqlServer
}
// --- end snippet ---

public static class Registration
{
    public static void Register(IServiceCollection services, DatabaseType dbType, string connectionString)
    {
        // --- snippet: "DI Configuration" ---
        // dbType and connectionString come from your configuration, e.g.
        // Enum.Parse<DatabaseType>(config["Database:Type"]) and config.GetConnectionString("EventStore")

        // Register the adapter based on config. A singleton: the adapter holds the connection pool
        services.AddSingleton<IEventStoreAdapterFactory, EventStoreAdapterFactory>();
        services.AddSingleton<IEventStoreAdapter>(sp =>
            sp.GetRequiredService<IEventStoreAdapterFactory>().CreateAdapter(dbType, connectionString));

        // Register event store
        services.AddSingleton<IEventStore>(sp =>
            new EventStore(
                sp.GetRequiredService<IEventStoreAdapter>(),
                new JsonEventSerializer(),
                new OrderEventTypeRegistry()));

        // Register repository
        services.AddScoped<IAggregateRepository<Order, OrderId>>(sp =>
            new AggregateRepository<Order, OrderId>(
                sp.GetRequiredService<IEventStore>(),
                () => new Order(),
                id => new StreamId($"order-{id.Value}")));
        // --- end snippet ---
    }

    public static async Task CompleteSetupAsync(IServiceCollection services)
    {
        // --- snippet: "Complete Setup Example" ---
        // PostgreSQL
        services
            .AddSingleton<IEventTypeRegistry, OrderEventTypeRegistry>()
            .AddSingleton<IEventSerializer, JsonEventSerializer>()  // or ZeroAlloc.Serialisation, see events.md
            .AddEventSourcing()
            .UsePostgreSqlEventStore("Host=localhost;Database=EventStore;Username=postgres;Password=password")
            .UsePostgreSqlSnapshotStore<OrderState>("Host=localhost;Database=EventStore;Username=postgres;Password=password")
            .UseAggregateRepository<Order, OrderId>(
                () => new Order(),
                id => new StreamId($"order-{id.Value}"));

        // SQL Server: the same, with
        //     .UseSqlServerEventStore(cs)
        //     .UseSqlServerSnapshotStore<OrderState>(cs)

        var sp = services.BuildServiceProvider();

        // The registrations do not create tables: create them once at startup
        await ((PostgreSqlEventStoreAdapter)sp.GetRequiredService<IEventStoreAdapter>()).EnsureSchemaAsync();
        await ((PostgreSqlSnapshotStore<OrderState>)sp.GetRequiredService<ISnapshotStore<OrderState>>()).EnsureSchemaAsync();

        var repository = sp.GetRequiredService<IAggregateRepository<Order, OrderId>>();

        var orderId = new OrderId(Guid.NewGuid());
        using var order = new Order();
        order.Place("alice");
        var saved = await repository.SaveAsync(order, orderId);
        if (saved.IsFailure)
            throw new InvalidOperationException(saved.Error.ToString());  // e.g. [CONFLICT] on a concurrent write
        // --- end snippet ---
    }
}
