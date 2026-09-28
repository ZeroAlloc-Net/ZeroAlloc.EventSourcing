using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests;

// An application-side domain model. ApplyEvent and OrderEventTypeRegistry come from the
// source generator, exactly as they would in an adopter's project.

public readonly record struct OrderId(Guid Value);

public sealed record OrderPlaced(string Customer);
public sealed record ItemAdded(decimal Price);
public sealed record OrderShipped(string Tracking);

public partial struct OrderState : IAggregateState<OrderState>
{
    public static OrderState Initial => default;

    public bool IsPlaced { get; private set; }
    public bool IsShipped { get; private set; }
    public int Items { get; private set; }
    public decimal Total { get; private set; }

    internal OrderState Apply(OrderPlaced e) => this with { IsPlaced = true };
    internal OrderState Apply(ItemAdded e) => this with { Items = Items + 1, Total = Total + e.Price };
    internal OrderState Apply(OrderShipped e) => this with { IsShipped = true };
}

public sealed partial class Order : Aggregate<OrderId, OrderState>
{
    public void SetId(OrderId id) => Id = id;
    public void Place(string customer) => Raise(new OrderPlaced(customer));
    public void AddItem(decimal price) => Raise(new ItemAdded(price));
    public void Ship(string tracking) => Raise(new OrderShipped(tracking));
}

/// <summary>Reflection-based JSON is fine in a test; the AOT smoke covers the AOT path.</summary>
internal sealed class JsonEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
        => System.Text.Json.JsonSerializer.Deserialize(payload.Span, eventType)!;
}

/// <summary>
/// Passes everything through to the real store and records where each read started, so a test
/// can tell a snapshot load, which reads from the snapshot position, from a full replay, which
/// reads from <see cref="StreamPosition.Start"/>.
/// </summary>
internal sealed class ReadRecordingEventStore(IEventStore inner) : IEventStore
{
    public List<StreamPosition> ReadsFrom { get; } = [];

    public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
        StreamId id, ReadOnlyMemory<object> events, StreamPosition expectedVersion, CancellationToken ct = default)
        => inner.AppendAsync(id, events, expectedVersion, ct);

    public IAsyncEnumerable<EventEnvelope> ReadAsync(StreamId id, StreamPosition from = default, CancellationToken ct = default)
    {
        ReadsFrom.Add(from);
        return inner.ReadAsync(id, from, ct);
    }

    public ValueTask<IEventSubscription> SubscribeAsync(
        StreamId id, StreamPosition from, Func<EventEnvelope, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        => inner.SubscribeAsync(id, from, handler, ct);
}
