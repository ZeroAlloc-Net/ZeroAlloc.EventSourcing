using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.EventSourcing;

// The "Testing with Mocked Dependencies" section of docs/testing/testing-projections.md, copied as
// it appears there between "--- snippet ---" markers and compiled against the public API, with
// the page's helper and event it relies on. The test runs. When a snippet changes in the docs,
// change it here as well. See issue #410.
namespace ZeroAlloc.EventSourcing.Aggregates.ConsumerTests.TestingProjectionsDocs;

// From the page's "Helper Methods for Tests" and "Domain Models" sections
public record OrderPlacedEvent(string OrderId, decimal Amount);

public static class ProjectionTestHelper
{
    public static EventEnvelope MakeEnvelope(
        StreamId? streamId = null,
        StreamPosition? position = null,
        object? @event = null,
        EventMetadata? metadata = null)
    {
        return new EventEnvelope(
            StreamId: streamId ?? new StreamId("test-stream"),
            Position: position ?? new StreamPosition(1),
            Event: @event ?? new object(),
            Metadata: metadata ?? EventMetadata.New("TestEvent")
        );
    }

    public static EventEnvelope MakeEnvelope<TEvent>(
        TEvent @event,
        StreamId? streamId = null,
        StreamPosition? position = null) where TEvent : notnull
    {
        return MakeEnvelope(streamId, position, @event);
    }
}

// --- snippet: "Testing with Mocked Dependencies" ---
public sealed record Order(string OrderId, string CustomerName, string Status);

public sealed record EnrichedOrderReadModel(string OrderId, decimal Amount, string? CustomerName, string? Status);

public interface IOrderService
{
    Task<Order?> GetOrderAsync(string orderId);
}

public sealed class EnrichedOrderProjection : Projection<EnrichedOrderReadModel>
{
    private readonly IOrderService _orderService;

    public EnrichedOrderProjection(IOrderService orderService)
    {
        _orderService = orderService;
        Current = new EnrichedOrderReadModel(string.Empty, 0m, null, null);
    }

    // Apply is synchronous, so the lookup happens in HandleAsync, which is async and virtual
    public override async ValueTask HandleAsync(EventEnvelope @event, CancellationToken ct = default)
    {
        if (@event.Event is OrderPlacedEvent e)
        {
            var order = await _orderService.GetOrderAsync(e.OrderId);
            Current = new EnrichedOrderReadModel(e.OrderId, e.Amount, order?.CustomerName, order?.Status);
            return;
        }

        await base.HandleAsync(@event, ct);
    }

    protected override EnrichedOrderReadModel Apply(EnrichedOrderReadModel current, EventEnvelope @event)
        => current;
}

public class EnrichedOrderProjectionTests
{
    [Fact]
    public async Task HandleAsync_WithMockedService_EnrichesData()
    {
        // Arrange
        var orderService = Substitute.For<IOrderService>();
        orderService.GetOrderAsync("ORD-001")
            .Returns(new Order("ORD-001", "John Doe", "Placed"));

        var projection = new EnrichedOrderProjection(orderService);
        var @event = new OrderPlacedEvent("ORD-001", 100m);

        // Act
        await projection.HandleAsync(ProjectionTestHelper.MakeEnvelope(@event));

        // Assert
        projection.Current.CustomerName.Should().Be("John Doe");
        await orderService.Received(1).GetOrderAsync("ORD-001");
    }
}
// --- end snippet ---
