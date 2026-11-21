using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Serialisation;
using ZeroAlloc.Serialisation.SystemTextJson;

namespace ZeroAlloc.EventSourcing.Tests;

// --- ReplayableProjection with an ISerializer, issue #415 ---

// camelCase, so a saved state shows that the serializer wrote it rather than reflection-based
// System.Text.Json, which would write PascalCase property names.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CustomerTotals))]
[JsonSerializable(typeof(OrderReadModel))]
[JsonSerializable(typeof(System.Collections.Immutable.ImmutableDictionary<string, decimal>))]
[JsonSerializable(typeof(int))]
internal sealed partial class ReplayableProjectionJsonContext : JsonSerializerContext;

/// <summary>The serializers the test projections pass to the <see cref="ReplayableProjection{TReadModel}"/> constructor.</summary>
internal static class TestProjectionSerializers
{
    public static SystemTextJsonSerializer<OrderReadModel> OrderReadModel { get; }
        = new(ReplayableProjectionJsonContext.Default.OrderReadModel);

    public static SystemTextJsonSerializer<System.Collections.Immutable.ImmutableDictionary<string, decimal>> OrderAmounts { get; }
        = new(ReplayableProjectionJsonContext.Default.ImmutableDictionaryStringDecimal);

    public static SystemTextJsonSerializer<CustomerTotals> CustomerTotals { get; }
        = new(ReplayableProjectionJsonContext.Default.CustomerTotals);

    public static SystemTextJsonSerializer<int> Int32 { get; }
        = new(ReplayableProjectionJsonContext.Default.Int32);
}

public sealed class SerializedCustomerTotalsProjection : ReplayableProjection<CustomerTotals>
{
    public SerializedCustomerTotalsProjection(ISerializer<CustomerTotals> serializer)
        : base(CustomerTotals.Empty, serializer)
    {
    }

    public override string GetProjectionKey() => "serialized-customer-totals";

    protected override CustomerTotals Apply(CustomerTotals current, EventEnvelope @event) => @event.Event switch
    {
        OrderPlacedEvent e => current with { Total = current.Total + e.Amount, OrderCount = current.OrderCount + 1 },
        _ => current
    };
}

/// <summary>Writes bytes that are not valid UTF-8, the way a binary serializer can.</summary>
internal sealed class BinaryCustomerTotalsSerializer : ISerializer<CustomerTotals>
{
    public void Serialize(IBufferWriter<byte> writer, CustomerTotals value) => writer.Write(new byte[] { 0x01, 0xFF, 0xFE });

    public CustomerTotals? Deserialize(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
}

public class ReplayableProjectionSerializerTests
{
    private static SystemTextJsonSerializer<CustomerTotals> JsonSerializer()
        => new(ReplayableProjectionJsonContext.Default.CustomerTotals);

    private static async Task<IEventStore> StoreWith(StreamId streamId, params object[] events)
    {
        var eventStore = new EventStore(new InMemoryEventStoreAdapter(), new TestEventSerializer(), new TestEventTypeRegistry());
        await eventStore.AppendAsync(streamId, events.AsMemory(), StreamPosition.Start);
        return eventStore;
    }

    [Fact]
    public void Constructor_StartsAtTheInitialState()
    {
        new SerializedCustomerTotalsProjection(JsonSerializer()).Current.Should().BeSameAs(CustomerTotals.Empty);
    }

    [Fact]
    public void Constructor_NullSerializer_Throws()
    {
        var act = () => new SerializedCustomerTotalsProjection(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("serializer");
    }

    [Fact]
    public async Task Rebuild_SavesTheStateWithTheSerializer()
    {
        var streamId = new StreamId("customer-1");
        var eventStore = await StoreWith(streamId, new OrderPlacedEvent("A", 10m), new OrderPlacedEvent("B", 5m));
        var projectionStore = new InMemoryProjectionStore();
        var projection = new SerializedCustomerTotalsProjection(JsonSerializer());

        await projection.RebuildAsync(projectionStore, streamId, eventStore);
        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        var expected = new CustomerTotals(string.Empty, 15m, 2);
        projection.Current.Should().Be(expected);
        var saved = await projectionStore.LoadAsync("serialized-customer-totals");
        Encoding.UTF8.GetString(saved!.Value.Span).Should().Be("""{"customerId":"","total":15,"orderCount":2}""");
        System.Text.Json.JsonSerializer.Deserialize(saved.Value.Span, ReplayableProjectionJsonContext.Default.CustomerTotals)
            .Should().Be(expected);
    }

    /// <summary>
    /// A binary serializer's output, which is not valid UTF-8, is saved byte for byte. Until #425 the
    /// rebuild decoded the output as strict UTF-8 and threw.
    /// </summary>
    [Fact]
    public async Task Rebuild_BinarySerializer_SavesItsBytesUnchanged()
    {
        var streamId = new StreamId("customer-1");
        var eventStore = await StoreWith(streamId, new OrderPlacedEvent("A", 10m));
        var projectionStore = new InMemoryProjectionStore();
        var projection = new SerializedCustomerTotalsProjection(new BinaryCustomerTotalsSerializer());

        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        var saved = await projectionStore.LoadAsync("serialized-customer-totals");
        saved!.Value.ToArray().Should().Equal(new byte[] { 0x01, 0xFF, 0xFE });
    }
}
