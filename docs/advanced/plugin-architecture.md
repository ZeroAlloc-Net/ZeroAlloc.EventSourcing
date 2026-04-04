# Plugin Architecture

**Version:** 1.0  
**Last Updated:** 2026-04-04

## Overview

Building extensible ZeroAlloc.EventSourcing applications requires a plugin architecture that allows third-party extensions without modifying core code.

This guide shows how to design and implement a plugin system.

## Architecture: Plugin Interfaces

Define clear extension points:

```csharp
namespace ZeroAlloc.EventSourcing.Plugins;

/// <summary>
/// Core plugin interface. All plugins implement this.
/// </summary>
public interface IPlugin
{
    /// <summary>Unique plugin identifier.</summary>
    string Id { get; }

    /// <summary>Plugin version.</summary>
    Version Version { get; }

    /// <summary>
    /// Initialize plugin with configuration.
    /// Called once during startup.
    /// </summary>
    ValueTask InitializeAsync(IPluginContext context);

    /// <summary>
    /// Cleanup plugin resources.
    /// Called on shutdown.
    /// </summary>
    ValueTask ShutdownAsync();
}

/// <summary>
/// Plugin context: provides access to services.
/// </summary>
public interface IPluginContext
{
    /// <summary>Get a service by interface.</summary>
    T GetService<T>(string? name = null) where T : notnull;

    /// <summary>Register a service.</summary>
    void RegisterService<T>(T service, string? name = null) where T : notnull;
}

/// <summary>
/// Extension point for custom event stores.
/// </summary>
public interface IEventStorePlugin : IPlugin
{
    /// <summary>Create event store adapter.</summary>
    IEventStoreAdapter CreateAdapter();
}

/// <summary>
/// Extension point for custom snapshot stores.
/// </summary>
public interface ISnapshotStorePlugin : IPlugin
{
    /// <summary>Create snapshot store for aggregate state type.</summary>
    object CreateSnapshotStore(Type stateType);
}

/// <summary>
/// Extension point for projection processors.
/// </summary>
public interface IProjectionPlugin : IPlugin
{
    /// <summary>Create projection processor.</summary>
    IProjection CreateProjection();
}

/// <summary>
/// Extension point for event serializers.
/// </summary>
public interface ISerializationPlugin : IPlugin
{
    /// <summary>Create event serializer.</summary>
    IEventSerializer CreateSerializer();
}

/// <summary>
/// Extension point for custom behavior.
/// </summary>
public interface IMiddlewarePlugin : IPlugin
{
    /// <summary>Create middleware component.</summary>
    IMiddleware CreateMiddleware();
}

/// <summary>
/// Middleware for intercepting operations.
/// </summary>
public interface IMiddleware
{
    /// <summary>Intercept append operation.</summary>
    ValueTask<T> InterceptAppendAsync<T>(
        Func<ValueTask<T>> next,
        string operation
    );

    /// <summary>Intercept read operation.</summary>
    ValueTask<T> InterceptReadAsync<T>(
        Func<ValueTask<T>> next,
        string operation
    );
}
```

## Plugin Manager

```csharp
public class PluginManager : IPluginContext
{
    private readonly Dictionary<string, IPlugin> _plugins = new();
    private readonly Dictionary<string, object> _services = new();
    private readonly ILogger _logger;

    public PluginManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Register a plugin.</summary>
    public void RegisterPlugin(IPlugin plugin)
    {
        if (_plugins.ContainsKey(plugin.Id))
            throw new InvalidOperationException($"Plugin {plugin.Id} already registered");

        _plugins[plugin.Id] = plugin;
        _logger.LogInformation($"Registered plugin: {plugin.Id} v{plugin.Version}");
    }

    /// <summary>Initialize all registered plugins.</summary>
    public async ValueTask InitializeAsync()
    {
        foreach (var plugin in _plugins.Values)
        {
            try
            {
                await plugin.InitializeAsync(this);
                _logger.LogInformation($"Initialized plugin: {plugin.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error initializing plugin {plugin.Id}: {ex}");
                throw;
            }
        }
    }

    /// <summary>Shutdown all plugins.</summary>
    public async ValueTask ShutdownAsync()
    {
        foreach (var plugin in _plugins.Values.Reverse())
        {
            try
            {
                await plugin.ShutdownAsync();
                _logger.LogInformation($"Shutdown plugin: {plugin.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error shutting down plugin {plugin.Id}: {ex}");
            }
        }
    }

    /// <summary>Get plugin by ID.</summary>
    public IPlugin? GetPlugin(string id)
    {
        _plugins.TryGetValue(id, out var plugin);
        return plugin;
    }

    /// <summary>Get all plugins of type T.</summary>
    public IEnumerable<T> GetPlugins<T>() where T : IPlugin
        => _plugins.Values.OfType<T>();

    // IPluginContext implementation
    public T GetService<T>(string? name = null) where T : notnull
    {
        var key = string.IsNullOrEmpty(name) ? typeof(T).FullName! : name;

        if (!_services.TryGetValue(key, out var service))
            throw new InvalidOperationException($"Service {typeof(T).Name} not found");

        return (T)service;
    }

    public void RegisterService<T>(T service, string? name = null) where T : notnull
    {
        var key = string.IsNullOrEmpty(name) ? typeof(T).FullName! : name;
        _services[key] = service;
    }
}
```

## Example Plugin 1: SQL Server Event Store Plugin

```csharp
public class SqlServerEventStorePlugin : IEventStorePlugin
{
    public string Id => "sql-server-event-store";
    public Version Version => new(1, 0, 0);

    private string _connectionString;

    public async ValueTask InitializeAsync(IPluginContext context)
    {
        // Get configuration from context or environment
        _connectionString = Environment.GetEnvironmentVariable("EventStoreConnectionString") 
            ?? throw new InvalidOperationException("EventStoreConnectionString not configured");

        // Initialize database schema
        var adapter = CreateAdapter();
        if (adapter is SqlServerEventStoreAdapter sqlAdapter)
        {
            await sqlAdapter.InitializeAsync();
        }
    }

    public ValueTask ShutdownAsync() => default;

    public IEventStoreAdapter CreateAdapter()
        => new SqlServerEventStoreAdapter(_connectionString);
}

// Usage
var pluginManager = new PluginManager(logger);
pluginManager.RegisterPlugin(new SqlServerEventStorePlugin());
await pluginManager.InitializeAsync();

// Get event store adapters from all registered plugins
var eventStorePlugins = pluginManager.GetPlugins<IEventStorePlugin>();
var adapter = eventStorePlugins.FirstOrDefault()?.CreateAdapter() 
    ?? throw new InvalidOperationException("No event store plugin registered");
```

## Example Plugin 2: Metrics/Monitoring Plugin

```csharp
public class MetricsPlugin : IMiddlewarePlugin
{
    public string Id => "metrics";
    public Version Version => new(1, 0, 0);

    private MetricsCollector _collector;

    public async ValueTask InitializeAsync(IPluginContext context)
    {
        _collector = new MetricsCollector();
        context.RegisterService(_collector);
    }

    public ValueTask ShutdownAsync()
        => _collector.ReportAsync();

    public IMiddleware CreateMiddleware()
        => new MetricsMiddleware(_collector);

    private class MetricsMiddleware : IMiddleware
    {
        private readonly MetricsCollector _collector;

        public MetricsMiddleware(MetricsCollector collector)
        {
            _collector = collector;
        }

        public async ValueTask<T> InterceptAppendAsync<T>(
            Func<ValueTask<T>> next,
            string operation)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await next();
                _collector.RecordAppend(sw.Elapsed, success: true);
                return result;
            }
            catch
            {
                _collector.RecordAppend(sw.Elapsed, success: false);
                throw;
            }
        }

        public async ValueTask<T> InterceptReadAsync<T>(
            Func<ValueTask<T>> next,
            string operation)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await next();
                _collector.RecordRead(sw.Elapsed, success: true);
                return result;
            }
            catch
            {
                _collector.RecordRead(sw.Elapsed, success: false);
                throw;
            }
        }
    }

    public class MetricsCollector
    {
        private int _appendCount = 0;
        private int _appendErrors = 0;
        private double _appendTotalMs = 0;
        private int _readCount = 0;
        private int _readErrors = 0;
        private double _readTotalMs = 0;

        public void RecordAppend(TimeSpan duration, bool success)
        {
            _appendCount++;
            _appendTotalMs += duration.TotalMilliseconds;
            if (!success) _appendErrors++;
        }

        public void RecordRead(TimeSpan duration, bool success)
        {
            _readCount++;
            _readTotalMs += duration.TotalMilliseconds;
            if (!success) _readErrors++;
        }

        public async ValueTask ReportAsync()
        {
            var report = $@"
=== Event Sourcing Metrics ===
Append Operations: {_appendCount} ({_appendErrors} errors)
  Average: {(_appendCount > 0 ? _appendTotalMs / _appendCount : 0):F2} ms

Read Operations: {_readCount} ({_readErrors} errors)
  Average: {(_readCount > 0 ? _readTotalMs / _readCount : 0):F2} ms
";
            Console.WriteLine(report);
        }
    }
}
```

## Example Plugin 3: Audit Logging Plugin

```csharp
public class AuditLoggingPlugin : IMiddlewarePlugin
{
    public string Id => "audit-logging";
    public Version Version => new(1, 0, 0);

    private AuditLog _auditLog;

    public async ValueTask InitializeAsync(IPluginContext context)
    {
        _auditLog = new AuditLog();
        context.RegisterService(_auditLog);
    }

    public ValueTask ShutdownAsync() => default;

    public IMiddleware CreateMiddleware()
        => new AuditLoggingMiddleware(_auditLog);

    private class AuditLoggingMiddleware : IMiddleware
    {
        private readonly AuditLog _log;

        public AuditLoggingMiddleware(AuditLog log)
        {
            _log = log;
        }

        public async ValueTask<T> InterceptAppendAsync<T>(
            Func<ValueTask<T>> next,
            string operation)
        {
            _log.LogAppend(operation, "start");
            try
            {
                var result = await next();
                _log.LogAppend(operation, "success");
                return result;
            }
            catch (Exception ex)
            {
                _log.LogAppend(operation, $"error: {ex.Message}");
                throw;
            }
        }

        public async ValueTask<T> InterceptReadAsync<T>(
            Func<ValueTask<T>> next,
            string operation)
        {
            _log.LogRead(operation, "start");
            try
            {
                var result = await next();
                _log.LogRead(operation, "success");
                return result;
            }
            catch (Exception ex)
            {
                _log.LogRead(operation, $"error: {ex.Message}");
                throw;
            }
        }
    }

    public class AuditLog
    {
        private readonly List<AuditEntry> _entries = new();

        public void LogAppend(string operation, string status)
            => _entries.Add(new AuditEntry 
            { 
                Type = "append", 
                Operation = operation, 
                Status = status, 
                Timestamp = DateTime.UtcNow 
            });

        public void LogRead(string operation, string status)
            => _entries.Add(new AuditEntry 
            { 
                Type = "read", 
                Operation = operation, 
                Status = status, 
                Timestamp = DateTime.UtcNow 
            });

        public List<AuditEntry> GetEntries() => _entries.ToList();
    }

    public class AuditEntry
    {
        public string Type { get; set; }
        public string Operation { get; set; }
        public string Status { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
```

## Example Plugin 4: Custom Serialization Plugin

```csharp
public class MessagePackSerializationPlugin : ISerializationPlugin
{
    public string Id => "msgpack-serialization";
    public Version Version => new(1, 0, 0);

    public async ValueTask InitializeAsync(IPluginContext context)
    {
        var serializer = CreateSerializer();
        context.RegisterService<IEventSerializer>(serializer);
    }

    public ValueTask ShutdownAsync() => default;

    public IEventSerializer CreateSerializer()
        => new MessagePackEventSerializer();

    private class MessagePackEventSerializer : IEventSerializer
    {
        public string Serialize(object @event)
        {
            var bytes = MessagePackSerializer.Serialize(@event);
            return Convert.ToBase64String(bytes);
        }

        public object Deserialize(string data, Type eventType)
        {
            var bytes = Convert.FromBase64String(data);
            return MessagePackSerializer.Deserialize(eventType, bytes);
        }
    }
}
```

## Composition: Using Multiple Plugins

```csharp
public class Program
{
    public static async Task Main()
    {
        var logger = new ConsoleLogger();
        var pluginManager = new PluginManager(logger);

        // Register plugins
        pluginManager.RegisterPlugin(new SqlServerEventStorePlugin());
        pluginManager.RegisterPlugin(new MetricsPlugin());
        pluginManager.RegisterPlugin(new AuditLoggingPlugin());

        // Initialize all plugins
        await pluginManager.InitializeAsync();

        // Create event store using plugin
        var eventStorePlugins = pluginManager.GetPlugins<IEventStorePlugin>();
        var adapter = eventStorePlugins.First().CreateAdapter();
        var eventStore = new EventStore(adapter, new JsonEventSerializer());

        // Create middleware chain
        var middlewares = pluginManager.GetPlugins<IMiddlewarePlugin>()
            .Select(p => p.CreateMiddleware())
            .ToList();

        var wrappedEventStore = WrapWithMiddleware(eventStore, middlewares);

        // Use event store with all plugins active
        await wrappedEventStore.AppendAsync(...);

        // Shutdown
        await pluginManager.ShutdownAsync();
    }

    private static IEventStore WrapWithMiddleware(
        IEventStore eventStore,
        List<IMiddleware> middlewares)
    {
        // Create middleware chain
        // Each middleware wraps the next
        return new MiddlewareEventStore(eventStore, middlewares);
    }
}
```

## Plugin Discovery

Automatically discover plugins:

```csharp
public class PluginDiscovery
{
    /// <summary>Load plugins from assemblies in a directory.</summary>
    public static IEnumerable<IPlugin> DiscoverPlugins(string pluginDirectory)
    {
        var pluginAssemblies = Directory
            .GetFiles(pluginDirectory, "*.Plugin.dll")
            .Select(Assembly.LoadFrom);

        var plugins = new List<IPlugin>();

        foreach (var assembly in pluginAssemblies)
        {
            var pluginTypes = assembly
                .GetTypes()
                .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface);

            foreach (var pluginType in pluginTypes)
            {
                try
                {
                    var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
                    plugins.Add(plugin);
                }
                catch (Exception ex)
                {
                    // Log error but continue
                    Console.WriteLine($"Error loading plugin {pluginType.Name}: {ex}");
                }
            }
        }

        return plugins;
    }

    /// <summary>Load plugins from NuGet packages.</summary>
    public static async Task<IEnumerable<IPlugin>> DiscoverPluginsFromNuGetAsync(
        string pluginPackageName,
        string version)
    {
        // Use NuGet API to download package
        // Extract plugins from package
        // Load and return plugins
        throw new NotImplementedException();
    }
}

// Usage
var pluginManager = new PluginManager(logger);
var discoveredPlugins = PluginDiscovery.DiscoverPlugins("./plugins");

foreach (var plugin in discoveredPlugins)
{
    pluginManager.RegisterPlugin(plugin);
}

await pluginManager.InitializeAsync();
```

## Plugin Configuration

```csharp
public interface IPluginConfiguration
{
    T GetSetting<T>(string key);
}

public class PluginConfiguration : IPluginConfiguration
{
    private readonly Dictionary<string, string> _settings = new();

    public void LoadFromJson(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var config = JsonDocument.Parse(json);

        foreach (var property in config.RootElement.EnumerateObject())
        {
            _settings[property.Name] = property.Value.GetRawText();
        }
    }

    public T GetSetting<T>(string key)
    {
        if (!_settings.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Setting {key} not found");

        return JsonSerializer.Deserialize<T>(value)!;
    }
}

// plugins.json
{
    "sql-server-event-store": {
        "connection-string": "Server=.;Database=EventStore;"
    },
    "metrics": {
        "enabled": true,
        "report-interval": "00:01:00"
    }
}

// Usage
var config = new PluginConfiguration();
config.LoadFromJson("plugins.json");

var sqlPlugin = new SqlServerEventStorePlugin();
var connectionString = config.GetSetting<string>("sql-server-event-store:connection-string");
```

## Testing Plugins

```csharp
[TestClass]
public class PluginTests
{
    [TestMethod]
    public async Task Plugin_Initializes()
    {
        var pluginManager = new PluginManager(new NullLogger());
        var plugin = new MetricsPlugin();

        pluginManager.RegisterPlugin(plugin);
        await pluginManager.InitializeAsync();

        var metricsService = pluginManager.GetService<MetricsPlugin.MetricsCollector>();
        Assert.IsNotNull(metricsService);
    }

    [TestMethod]
    public async Task PluginManager_LoadsMultiplePlugins()
    {
        var pluginManager = new PluginManager(new NullLogger());
        pluginManager.RegisterPlugin(new SqlServerEventStorePlugin());
        pluginManager.RegisterPlugin(new MetricsPlugin());

        await pluginManager.InitializeAsync();

        var eventStorePlugins = pluginManager.GetPlugins<IEventStorePlugin>();
        Assert.AreEqual(1, eventStorePlugins.Count());

        var middlewarePlugins = pluginManager.GetPlugins<IMiddlewarePlugin>();
        Assert.AreEqual(1, middlewarePlugins.Count());
    }
}
```

## Best Practices

1. **Define clear contracts** — Plugin interfaces should be stable
2. **Version plugins** — Track version for compatibility
3. **Document configuration** — Provide schema and examples
4. **Handle errors gracefully** — Don't let one plugin crash the system
5. **Provide lifecycle hooks** — Initialize, shutdown, reload
6. **Log extensively** — Help developers debug plugin issues
7. **Test in isolation** — Each plugin should be independently testable
8. **Use dependency injection** — Plugins should request services, not create them

## Summary

A plugin architecture enables:

1. **Extensibility** — Add features without modifying core
2. **Third-party integration** — Custom event stores, serializers, etc.
3. **Modularity** — Features can be enabled/disabled
4. **Testability** — Plugins can be tested in isolation
5. **Loose coupling** — Plugins depend on interfaces, not implementations

Key components:

- `IPlugin` — Base interface for all plugins
- `PluginManager` — Manages plugin lifecycle
- `IPluginContext` — Provides access to services
- Middleware pattern — Intercept operations
- Plugin discovery — Load plugins dynamically

## Next Steps

- **[Contributing](./contributing.md)** — Contributing guide for developers
- **[Advanced Patterns](./custom-projections.md)** — More advanced patterns
- **[Architecture Guide](../core-concepts/architecture.md)** — Overall system design
