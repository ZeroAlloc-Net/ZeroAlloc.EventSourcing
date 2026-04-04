using BenchmarkDotNet.Attributes;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.Benchmarks;

// --- Test domain model for benchmarking ---

internal readonly record struct OrderId(Guid Value);

internal record OrderPlacedEvent(string OrderId, decimal Total);
internal record OrderShippedEvent(string TrackingNumber);

internal partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;
    public bool IsPlaced { get; private set; }
    public bool IsShipped { get; private set; }
    public decimal Total { get; private set; }
    public string? OrderId { get; set; }
    public string? TrackingNumber { get; set; }

    internal OrderState Apply(OrderPlacedEvent e) => this with { IsPlaced = true, Total = e.Total };
    internal OrderState Apply(OrderShippedEvent e) => this with { IsShipped = true };
}

internal sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void Place(string orderId, decimal total) =>
        Raise(new OrderPlacedEvent(orderId, total));

    public void Ship(string tracking) =>
        Raise(new OrderShippedEvent(tracking));

    public void SetId(OrderId id) => Id = id;

    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlacedEvent e => state.Apply(e),
        OrderShippedEvent e => state.Apply(e),
        _ => state
    };
}

internal sealed class OrderEventTypeRegistry : IEventTypeRegistry
{
    private static readonly Dictionary<string, Type> Map = new()
    {
        [nameof(OrderPlacedEvent)] = typeof(OrderPlacedEvent),
        [nameof(OrderShippedEvent)] = typeof(OrderShippedEvent),
    };

    public bool TryGetType(string eventType, out Type? type) => Map.TryGetValue(eventType, out type);
    public string GetTypeName(Type type) => type.Name;
}

/// <summary>
/// Benchmarks for aggregate loading performance.
/// Measures performance of loading aggregates from event streams of varying sizes.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class AggregateLoadBenchmarks
{
    // Named constants for event counts per stream size
    private const int SmallStreamEventCount = 10;
    private const int MediumStreamEventCount = 100;
    private const int LargeStreamEventCount = 1000;

    private IEventStore _eventStore = null!;
    private AggregateRepository<Order, OrderId> _repository = null!;
    private OrderId _smallStreamOrderId;
    private OrderId _mediumStreamOrderId;
    private OrderId _largeStreamOrderId;

    /// <summary>
    /// Global setup for benchmarks.
    /// Pre-populates event streams with varying event counts for replay performance testing.
    /// Creates three separate aggregates with 10, 100, and 1000 events respectively.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        // Initialize event store and repository
        var adapter = new InMemoryEventStoreAdapter();
        var registry = new OrderEventTypeRegistry();
        var serializer = new BenchmarkSerializer();
        _eventStore = new EventStore(adapter, serializer, registry);

        _repository = new AggregateRepository<Order, OrderId>(
            _eventStore,
            () => new Order(),
            id => new StreamId($"order-{id.Value}")
        );

        // Create small stream (10 events)
        _smallStreamOrderId = new OrderId(Guid.NewGuid());
        var smallOrder = new Order();
        smallOrder.SetId(_smallStreamOrderId);
        for (int i = 0; i < SmallStreamEventCount; i++)
        {
            smallOrder.Place($"ORD-SMALL-{i}", 100m);
        }
        var smallResult = await _repository.SaveAsync(smallOrder, _smallStreamOrderId);
        if (!smallResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to save small stream aggregate during setup: {smallResult.Error}");
        }

        // Create medium stream (100 events)
        _mediumStreamOrderId = new OrderId(Guid.NewGuid());
        var mediumOrder = new Order();
        mediumOrder.SetId(_mediumStreamOrderId);
        for (int i = 0; i < MediumStreamEventCount; i++)
        {
            mediumOrder.Place($"ORD-MEDIUM-{i}", 200m);
        }
        var mediumResult = await _repository.SaveAsync(mediumOrder, _mediumStreamOrderId);
        if (!mediumResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to save medium stream aggregate during setup: {mediumResult.Error}");
        }

        // Create large stream (1000 events)
        _largeStreamOrderId = new OrderId(Guid.NewGuid());
        var largeOrder = new Order();
        largeOrder.SetId(_largeStreamOrderId);
        for (int i = 0; i < LargeStreamEventCount; i++)
        {
            largeOrder.Place($"ORD-LARGE-{i}", 300m);
        }
        var largeResult = await _repository.SaveAsync(largeOrder, _largeStreamOrderId);
        if (!largeResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to save large stream aggregate during setup: {largeResult.Error}");
        }
    }

    /// <summary>
    /// Benchmark: LoadAggregateSmallStream
    /// Measures aggregate loading performance from a small stream (10 events).
    /// </summary>
    [Benchmark]
    public async Task LoadAggregateSmallStream()
    {
        var result = await _repository.LoadAsync(_smallStreamOrderId, CancellationToken.None);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to load small stream aggregate: {result.Error}");
        }
    }

    /// <summary>
    /// Benchmark: LoadAggregateMediumStream
    /// Measures aggregate loading performance from a medium stream (100 events).
    /// </summary>
    [Benchmark]
    public async Task LoadAggregateMediumStream()
    {
        var result = await _repository.LoadAsync(_mediumStreamOrderId, CancellationToken.None);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to load medium stream aggregate: {result.Error}");
        }
    }

    /// <summary>
    /// Benchmark: LoadAggregateLargeStream
    /// Measures aggregate loading performance from a large stream (1000 events).
    /// </summary>
    [Benchmark]
    public async Task LoadAggregateLargeStream()
    {
        var result = await _repository.LoadAsync(_largeStreamOrderId, CancellationToken.None);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Failed to load large stream aggregate: {result.Error}");
        }
    }
}
