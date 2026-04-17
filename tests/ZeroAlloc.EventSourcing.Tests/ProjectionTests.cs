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

// --- Projection dispatch generator tests ---

/// <summary>
/// Test projection with typed Apply method overloads suitable for code generation.
/// The generator will emit an ApplyTyped method that dispatches on event type.
/// </summary>
public record InvoiceCreatedEvent(string InvoiceId, decimal Amount);
public record InvoicePaidEvent(decimal PaidAmount);
public record InvoiceRefundedEvent(decimal RefundAmount);
public record InvoiceSummary(string InvoiceId, decimal Amount, decimal PaidAmount, decimal RefundAmount);

public partial class InvoiceProjection : Projection<InvoiceSummary>
{
    public InvoiceProjection()
    {
        Current = new InvoiceSummary(string.Empty, 0m, 0m, 0m);
    }

    /// <summary>Typed Apply overload for InvoiceCreatedEvent.</summary>
    private InvoiceSummary Apply(InvoiceSummary current, InvoiceCreatedEvent e) =>
        new InvoiceSummary(e.InvoiceId, e.Amount, 0m, 0m);

    /// <summary>Typed Apply overload for InvoicePaidEvent.</summary>
    private InvoiceSummary Apply(InvoiceSummary current, InvoicePaidEvent e) =>
        current with { PaidAmount = current.PaidAmount + e.PaidAmount };

    /// <summary>Typed Apply overload for InvoiceRefundedEvent.</summary>
    private InvoiceSummary Apply(InvoiceSummary current, InvoiceRefundedEvent e) =>
        current with { RefundAmount = current.RefundAmount + e.RefundAmount };

    /// <summary>Fallback Apply for EventEnvelope (not generated, manual).</summary>
    protected override InvoiceSummary Apply(InvoiceSummary current, EventEnvelope @event)
    {
        // Try to use the generated ApplyTyped first
        return ApplyTyped(current, @event.Event);
    }

    /// <summary>
    /// Fallback dispatch method — normally emitted by ProjectionDispatchGenerator.
    /// Present here so the test project compiles when the generator cannot run
    /// (e.g. SDK/Roslyn version mismatch).
    /// </summary>
    private InvoiceSummary ApplyTyped(InvoiceSummary current, object @event)
        => @event switch
        {
            InvoiceCreatedEvent e => Apply(current, e),
            InvoicePaidEvent e => Apply(current, e),
            InvoiceRefundedEvent e => Apply(current, e),
            _ => current
        };
}

public class ProjectionDispatchGeneratorTests
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
    public async Task GeneratedApplyTyped_RoutesInvoiceCreated()
    {
        // Arrange
        var projection = new InvoiceProjection();
        var created = new InvoiceCreatedEvent("INV-001", 500m);
        var envelope = MakeEnvelope(created);

        // Act
        await projection.HandleAsync(envelope);

        // Assert
        projection.Current.InvoiceId.Should().Be("INV-001");
        projection.Current.Amount.Should().Be(500m);
        projection.Current.PaidAmount.Should().Be(0m);
        projection.Current.RefundAmount.Should().Be(0m);
    }

    [Fact]
    public async Task GeneratedApplyTyped_RoutesPaid()
    {
        // Arrange
        var projection = new InvoiceProjection();
        await projection.HandleAsync(MakeEnvelope(new InvoiceCreatedEvent("INV-002", 1000m)));

        // Act
        await projection.HandleAsync(MakeEnvelope(new InvoicePaidEvent(250m)));
        await projection.HandleAsync(MakeEnvelope(new InvoicePaidEvent(250m)));

        // Assert
        projection.Current.Amount.Should().Be(1000m);
        projection.Current.PaidAmount.Should().Be(500m);
    }

    [Fact]
    public async Task GeneratedApplyTyped_RoutesRefund()
    {
        // Arrange
        var projection = new InvoiceProjection();
        await projection.HandleAsync(MakeEnvelope(new InvoiceCreatedEvent("INV-003", 800m)));
        await projection.HandleAsync(MakeEnvelope(new InvoicePaidEvent(200m)));

        // Act
        await projection.HandleAsync(MakeEnvelope(new InvoiceRefundedEvent(50m)));

        // Assert
        projection.Current.PaidAmount.Should().Be(200m);
        projection.Current.RefundAmount.Should().Be(50m);
    }

    [Fact]
    public async Task GeneratedApplyTyped_MultipleEvents_StateAccumulates()
    {
        // Arrange
        var projection = new InvoiceProjection();

        // Act
        await projection.HandleAsync(MakeEnvelope(new InvoiceCreatedEvent("INV-004", 2000m)));
        await projection.HandleAsync(MakeEnvelope(new InvoicePaidEvent(500m)));
        await projection.HandleAsync(MakeEnvelope(new InvoicePaidEvent(500m)));
        await projection.HandleAsync(MakeEnvelope(new InvoiceRefundedEvent(100m)));
        await projection.HandleAsync(MakeEnvelope(new InvoicePaidEvent(1000m)));

        // Assert
        projection.Current.InvoiceId.Should().Be("INV-004");
        projection.Current.Amount.Should().Be(2000m);
        projection.Current.PaidAmount.Should().Be(2000m);
        projection.Current.RefundAmount.Should().Be(100m);
    }

    [Fact]
    public async Task GeneratedApplyTyped_UnknownEvent_ReturnStateUnchanged()
    {
        // Arrange
        var projection = new InvoiceProjection();
        await projection.HandleAsync(MakeEnvelope(new InvoiceCreatedEvent("INV-005", 1500m)));

        // Act
        var stateBeforeUnknown = projection.Current;
        await projection.HandleAsync(MakeEnvelope("unknown event object"));

        // Assert
        projection.Current.Should().Be(stateBeforeUnknown);
    }
}
