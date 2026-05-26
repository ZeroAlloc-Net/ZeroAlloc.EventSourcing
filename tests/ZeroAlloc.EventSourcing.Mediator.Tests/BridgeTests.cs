using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Mediator.Tests;

#pragma warning disable MA0048 // tests + supporting test types in a single file is intentional
#pragma warning disable CA1707 // test method names use underscores for readability

[Collection("BridgeTests")] // serialise tests — they share a process-wide TestSink
public sealed class BridgeTests
{
    private static IServiceCollection BuildServices(string streamId)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IEventTypeRegistry>(new BridgeTestTypeRegistry());
        services.AddSingleton<IEventSerializer>(new BridgeTestSerializer());

        services.AddEventSourcing()
            .UseInMemoryEventStore()
            .PublishViaMediator(new StreamId(streamId));   // generator-emitted, NO shim

        services.AddMediator();
        // MediatorService resolves handlers as concrete types via GetRequiredService<THandler>.
        // Register them explicitly so DI can construct them when Publish fires.
        services.AddTransient<UserCreatedHandler>();
        services.AddTransient<OrderPlacedThrowingHandler>();

        return services;
    }

    private static async Task<HostAdapter> StartHostAsync(IServiceCollection services)
    {
        var host = new HostAdapter(services.BuildServiceProvider());
        await host.StartAsync(default);
        return host;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Bridge_PublishesLiveEvent_ToMediator()
    {
        TestSink.Events.Clear();
        var services = BuildServices("users-t1");
        var host = await StartHostAsync(services);
        try
        {
            var store = host.Services.GetRequiredService<IEventStore>();

            var append = await store.AppendAsync(
                new StreamId("users-t1"),
                new object[] { new UserCreated("alice-t1") }.AsMemory(),
                StreamPosition.Start);
            append.IsSuccess.Should().BeTrue();

            await WaitForAsync(() => TestSink.Events.Contains("user:alice-t1"));
            TestSink.Events.Should().Contain("user:alice-t1");
        }
        finally
        {
            await host.StopAsync(default);
        }
    }

    [Fact]
    public async Task Bridge_DoesNotPublishHistory_FromBeforeSubscription()
    {
        // The load-bearing test: events appended BEFORE the host (and hence the bridge) starts
        // must NEVER reach Mediator. Subscription starts at StreamPosition.End.
        TestSink.Events.Clear();
        var services = BuildServices("users-t2");
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IEventStore>();

        // Append history BEFORE starting the host.
        var append = await store.AppendAsync(
            new StreamId("users-t2"),
            new object[] { new UserCreated("historic-t2") }.AsMemory(),
            StreamPosition.Start);
        append.IsSuccess.Should().BeTrue();

        // Now start the host — the bridge subscribes from End, so historic events are skipped.
        var host = new HostAdapter(provider);
        await host.StartAsync(default);
        try
        {
            // Give the subscription a moment to settle.
            await Task.Delay(100);

            // Append a NEW event AFTER startup — that one must be delivered.
            var append2 = await store.AppendAsync(
                new StreamId("users-t2"),
                new object[] { new UserCreated("live-t2") }.AsMemory(),
                new StreamPosition(1));
            append2.IsSuccess.Should().BeTrue();

            await WaitForAsync(() => TestSink.Events.Contains("user:live-t2"));

            TestSink.Events.Should().Contain("user:live-t2");
            TestSink.Events.Should().NotContain("user:historic-t2");
        }
        finally
        {
            await host.StopAsync(default);
        }
    }

    [Fact]
    public async Task Bridge_SkipsNonNotificationEvents()
    {
        TestSink.Events.Clear();
        var services = BuildServices("users-t3");
        var host = await StartHostAsync(services);
        try
        {
            var store = host.Services.GetRequiredService<IEventStore>();

            // Append a non-INotification event — bridge logs and skips.
            var append = await store.AppendAsync(
                new StreamId("users-t3"),
                new object[] { new PlainEvent("ignored-t3") }.AsMemory(),
                StreamPosition.Start);
            append.IsSuccess.Should().BeTrue();

            // Then append a real INotification — must still be delivered.
            var append2 = await store.AppendAsync(
                new StreamId("users-t3"),
                new object[] { new UserCreated("delivered-t3") }.AsMemory(),
                new StreamPosition(1));
            append2.IsSuccess.Should().BeTrue();

            await WaitForAsync(() => TestSink.Events.Contains("user:delivered-t3"));

            TestSink.Events.Should().Contain("user:delivered-t3");
        }
        finally
        {
            await host.StopAsync(default);
        }
    }

    [Fact]
    public async Task Bridge_ContinuesAfterHandlerThrows()
    {
        TestSink.Events.Clear();
        var services = BuildServices("orders-t4");
        var host = await StartHostAsync(services);
        try
        {
            var store = host.Services.GetRequiredService<IEventStore>();

            // OrderPlacedThrowingHandler throws after enqueuing — bridge swallows.
            var append = await store.AppendAsync(
                new StreamId("orders-t4"),
                new object[] { new OrderPlaced(99.99m) }.AsMemory(),
                StreamPosition.Start);
            append.IsSuccess.Should().BeTrue();

            await WaitForAsync(() => TestSink.Events.Contains("order-attempted:99.99"));

            // Subscription should still be alive — append a second event and observe it.
            var append2 = await store.AppendAsync(
                new StreamId("orders-t4"),
                new object[] { new OrderPlaced(11.11m) }.AsMemory(),
                new StreamPosition(1));
            append2.IsSuccess.Should().BeTrue();

            await WaitForAsync(() => TestSink.Events.Contains("order-attempted:11.11"));

            TestSink.Events.Should().Contain("order-attempted:99.99");
            TestSink.Events.Should().Contain("order-attempted:11.11");
        }
        finally
        {
            await host.StopAsync(default);
        }
    }

    [Fact]
    public async Task Bridge_StopAsync_DisposesSubscription()
    {
        TestSink.Events.Clear();
        var services = BuildServices("users-t5");
        var host = await StartHostAsync(services);

        var store = host.Services.GetRequiredService<IEventStore>();

        // Stop the host — bridge disposes its subscription.
        await host.StopAsync(default);

        // Events appended AFTER stop should NOT reach Mediator (subscription is gone).
        var append = await store.AppendAsync(
            new StreamId("users-t5"),
            new object[] { new UserCreated("after-stop-t5") }.AsMemory(),
            StreamPosition.Start);
        append.IsSuccess.Should().BeTrue();

        // Give any in-flight delivery a moment to settle.
        await Task.Delay(150);

        TestSink.Events.Should().NotContain("user:after-stop-t5");
    }

    /// <summary>
    /// Minimal host abstraction that starts/stops every registered IHostedService.
    /// Avoids dragging in the full Microsoft.Extensions.Hosting Host builder for unit tests.
    /// </summary>
    private sealed class HostAdapter : IHost
    {
        public HostAdapter(IServiceProvider services) => Services = services;

        public IServiceProvider Services { get; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var hs in Services.GetServices<IHostedService>())
                await hs.StartAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var hs in Services.GetServices<IHostedService>())
                await hs.StopAsync(cancellationToken);
        }

        public void Dispose() { /* services lifetime managed externally */ }
    }
}

[CollectionDefinition("BridgeTests", DisableParallelization = true)]
public sealed class BridgeTestsCollection { }

/// <summary>
/// JSON-backed serializer for tests. Uses System.Text.Json.
/// </summary>
internal sealed class BridgeTestSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

/// <summary>
/// Maps event-type names back to CLR types for round-tripping through the in-memory store.
/// </summary>
internal sealed class BridgeTestTypeRegistry : IEventTypeRegistry
{
    private static readonly Dictionary<string, Type> Map = new(StringComparer.Ordinal)
    {
        [nameof(UserCreated)] = typeof(UserCreated),
        [nameof(OrderPlaced)] = typeof(OrderPlaced),
        [nameof(PlainEvent)] = typeof(PlainEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => Map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}

#pragma warning restore CA1707
#pragma warning restore MA0048
