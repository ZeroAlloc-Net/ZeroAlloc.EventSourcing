using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

// --- test double ---

/// <summary>
/// Test batched projection that tracks flushed batches.
/// </summary>
public sealed class TestBatchedProjection : BatchedProjection<OrderReadModel>
{
    public List<List<EventEnvelope>> FlushedBatches { get; } = new();

    public TestBatchedProjection(int batchSize = 5) : base(batchSize)
    {
        Current = new OrderReadModel(string.Empty, 0m, null, false);
    }

    protected override bool IncludeEvent(EventEnvelope @event)
    {
        // Include all order events
        return @event.Event is OrderEvent;
    }

    protected override OrderReadModel Apply(OrderReadModel current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            OrderPlacedEvent e => new OrderReadModel(e.OrderId, e.Amount, null, false),
            OrderShippedEvent e => current with { TrackingCode = e.TrackingCode },
            OrderCancelledEvent => current with { IsCancelled = true },
            _ => current
        };
    }

    protected override ValueTask FlushBatchAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct = default)
    {
        // Record the batch for testing
        FlushedBatches.Add(new List<EventEnvelope>(batch));
        return default;
    }
}

// --- tests ---

/// <summary>
/// Unit tests for <see cref="BatchedProjection{TReadModel}"/>. Verifies that events are
/// accumulated and flushed at the configured batch size.
/// </summary>
public class BatchedProjectionTests
{
    private static EventEnvelope MakeEnvelope(StreamId streamId, StreamPosition position, object @event)
    {
        return new EventEnvelope(
            StreamId: streamId,
            Position: position,
            Event: @event,
            Metadata: EventMetadata.New("TestEvent"));
    }

    [Fact]
    public async Task HandleAsync_AccumulatesAndFlushesAtBatchSize()
    {
        // Arrange
        var projection = new TestBatchedProjection(batchSize: 3);
        var streamId = new StreamId("test-stream");

        // Act: Send 5 events with batch size 3
        // First 3 should trigger a flush
        // Events 4-5 should be pending
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(1), new OrderPlacedEvent("ORD-001", 100m)));
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(2), new OrderShippedEvent("ORD-001", "TRACK-001")));
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(3), new OrderPlacedEvent("ORD-002", 200m)));

        // After 3 events, first batch should be flushed
        projection.FlushedBatches.Should().HaveCount(1);
        projection.FlushedBatches[0].Should().HaveCount(3);

        // Add 2 more events (not yet flushed)
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(4), new OrderShippedEvent("ORD-002", "TRACK-002")));
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(5), new OrderCancelledEvent("ORD-001")));

        // Assert: Pending batch not yet flushed
        projection.FlushedBatches.Should().HaveCount(1);
    }

    [Fact]
    public async Task FlushAsync_FlushesPendingBatch()
    {
        // Arrange
        var projection = new TestBatchedProjection(batchSize: 5);
        var streamId = new StreamId("test-stream");

        // Act: Send 2 events (less than batch size)
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(1), new OrderPlacedEvent("ORD-001", 100m)));
        await projection.HandleAsync(MakeEnvelope(streamId, new StreamPosition(2), new OrderShippedEvent("ORD-001", "TRACK-001")));

        // No flush yet
        projection.FlushedBatches.Should().BeEmpty();

        // Act: Manually flush
        await projection.FlushAsync();

        // Assert: Pending batch is flushed
        projection.FlushedBatches.Should().HaveCount(1);
        projection.FlushedBatches[0].Should().HaveCount(2);
    }
}
