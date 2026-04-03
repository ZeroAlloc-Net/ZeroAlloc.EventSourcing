using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

// --- test domain model ---

/// <summary>Event raised when an order is placed.</summary>
public record OrderPlacedForProjection(string OrderId, decimal Amount);

/// <summary>Event raised when an order is shipped.</summary>
public record OrderShippedForProjection(string TrackingCode);

/// <summary>Read model that summarizes the state of an order.</summary>
public record OrderSummaryForProjection(string OrderId, decimal Amount, string? TrackingCode);

/// <summary>
/// Test projection for orders. Demonstrates how to implement <see cref="Projection{TReadModel}"/>
/// by switching on event types and returning updated state.
/// </summary>
public sealed class OrderProjectionForTest : Projection<OrderSummaryForProjection>
{
    /// <summary>Initializes the projection with an empty read model.</summary>
    public OrderProjectionForTest()
    {
        Current = new OrderSummaryForProjection(string.Empty, 0m, null);
    }

    /// <summary>Routes events to typed handlers and returns the updated read model.</summary>
    protected override OrderSummaryForProjection Apply(OrderSummaryForProjection current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedForProjection e => new OrderSummaryForProjection(e.OrderId, e.Amount, current.TrackingCode),
            OrderShippedForProjection e => current with { TrackingCode = e.TrackingCode },
            _ => current
        };
    }
}

// --- tests ---

/// <summary>
/// Unit tests for <see cref="Projection{TReadModel}"/>. Verifies that projections correctly
/// apply events to read models and accumulate state across multiple events.
/// </summary>
public class ProjectionTests
{
    private static EventEnvelope MakeEnvelope(object @event)
    {
        return new EventEnvelope(
            StreamId: new StreamId("test-stream"),
            Position: new StreamPosition(1),
            Event: @event,
            Metadata: EventMetadata.New("TestEvent"));
    }

    [Fact]
    public async Task Projection_AppliesEvents_UpdatesReadModel()
    {
        // Arrange
        var projection = new OrderProjectionForTest();
        var orderPlaced = new OrderPlacedForProjection("ORD-001", 99.99m);
        var envelope = MakeEnvelope(orderPlaced);

        // Act
        await projection.HandleAsync(envelope);

        // Assert
        projection.Current.Should().NotBeNull();
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(99.99m);
        projection.Current.TrackingCode.Should().BeNull();
    }

    [Fact]
    public async Task Projection_MultipleEvents_StateAccumulates()
    {
        // Arrange
        var projection = new OrderProjectionForTest();
        var orderPlaced = new OrderPlacedForProjection("ORD-002", 150.00m);
        var orderShipped = new OrderShippedForProjection("TRACK-12345");

        // Act
        await projection.HandleAsync(MakeEnvelope(orderPlaced));
        await projection.HandleAsync(MakeEnvelope(orderShipped));

        // Assert
        projection.Current.OrderId.Should().Be("ORD-002");
        projection.Current.Amount.Should().Be(150.00m);
        projection.Current.TrackingCode.Should().Be("TRACK-12345");
    }

    [Fact]
    public async Task Projection_UnknownEvent_Ignored()
    {
        // Arrange
        var projection = new OrderProjectionForTest();
        var orderPlaced = new OrderPlacedForProjection("ORD-003", 75.50m);
        var unknownEvent = "this is not a recognized event";

        // Act
        await projection.HandleAsync(MakeEnvelope(orderPlaced));
        var stateBeforeUnknown = projection.Current;
        await projection.HandleAsync(MakeEnvelope(unknownEvent));

        // Assert
        projection.Current.Should().Be(stateBeforeUnknown);
        projection.Current.OrderId.Should().Be("ORD-003");
        projection.Current.Amount.Should().Be(75.50m);
    }
}
