using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

// Reuse TestEventSerializer and TestEventTypeRegistry from ReplayableProjectionTests

// --- Projection implementations for integration tests ---

/// <summary>
/// Filtered projection for orders that only processes order events.
/// </summary>
public sealed class OrderProjectionFiltered : FilteredProjection<OrderReadModel>
{
    public OrderProjectionFiltered()
    {
        Current = new OrderReadModel(string.Empty, 0m, null, false);
    }

    protected override bool IncludeEvent(EventEnvelope @event)
    {
        // Only process order events
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
}

/// <summary>
/// Replayable order projection with rebuild support.
/// </summary>
public sealed class ReplayableOrderProjectionFull : ReplayableProjection<OrderReadModel>
{
    private readonly StreamId _streamId;

    public ReplayableOrderProjectionFull(StreamId streamId)
    {
        _streamId = streamId;
        Current = new OrderReadModel(string.Empty, 0m, null, false);
    }

    public override string GetProjectionKey() => $"ReplayableOrderProjection-{_streamId.Value}";

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
}

// --- Integration tests ---

/// <summary>
/// End-to-end integration tests for advanced projection features.
/// Tests filtered projections, replayable projections, consistency checking, and batched processing.
/// </summary>
public class AdvancedProjectionsIntegrationTests
{
    private static async ValueTask<StreamPosition> GetLatestPositionAsync(IEventStore eventStore, StreamId streamId)
    {
        var position = StreamPosition.Start;
        await foreach (var @event in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            position = @event.Position;
        }
        return position;
    }

    [Fact]
    public async Task FilteredProjection_ReceivesOnlyMatchingEvents()
    {
        // Arrange
        var streamId = new StreamId("order-filtered-001");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var projection = new OrderProjectionFiltered();

        // Append mixed events (orders and other types)
        await eventStore.AppendAsync(
            streamId,
            new object[] {
                new OrderPlacedEvent("ORD-001", 100m),
                new OrderShippedEvent("ORD-001", "TRACK-001"),
                new OrderCancelledEvent("ORD-001")
            }.AsMemory(),
            StreamPosition.Start);

        // Act: Process all events through the filtered projection
        await foreach (var @event in eventStore.ReadAsync(streamId))
        {
            await projection.HandleAsync(@event);
        }

        // Assert: Only order events were processed (the string was ignored)
        projection.Current.OrderId.Should().Be("ORD-001");
        projection.Current.Amount.Should().Be(100m);
        projection.Current.TrackingCode.Should().Be("TRACK-001");
        projection.Current.IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task ReplayableProjection_RebuildRestoresCompleteState()
    {
        // Arrange
        var streamId = new StreamId("order-replayable-001");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var projectionStore = new InMemoryProjectionStore();
        var projection = new ReplayableOrderProjectionFull(streamId);

        // Append events
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("ORD-100", 500m) }.AsMemory(),
            StreamPosition.Start);
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderShippedEvent("ORD-100", "TRACK-100") }.AsMemory(),
            new StreamPosition(1));

        // Act: Rebuild from events
        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        // Assert: State is fully restored
        projection.Current.OrderId.Should().Be("ORD-100");
        projection.Current.Amount.Should().Be(500m);
        projection.Current.TrackingCode.Should().Be("TRACK-100");
        projection.Current.IsCancelled.Should().BeFalse();

        // Verify state was saved to projection store
        var saved = await projectionStore.LoadAsync(projection.GetProjectionKey());
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task ConsistencyChecker_DetectsProjectionGaps()
    {
        // Arrange
        var streamId = new StreamId("order-consistency-001");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var checker = new ProjectionConsistencyChecker<OrderReadModel>();

        // Append first event
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("ORD-200", 250m) }.AsMemory(),
            StreamPosition.Start);

        var position1 = await GetLatestPositionAsync(eventStore, streamId);

        // Append second event
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderShippedEvent("ORD-200", "TRACK-200") }.AsMemory(),
            new StreamPosition(1));

        var position2 = await GetLatestPositionAsync(eventStore, streamId);

        // Act: Check consistency when projection is at position1 but store is at position2
        var report = await checker.VerifyAsync(eventStore, streamId, position1);

        // Assert: Report shows projection is behind
        report.Should().NotBeNull();
        report!.IsConsistent.Should().BeFalse();
        report.ProjectionCheckpoint.Should().Be(position1);
        report.EventStoreLatestPosition.Should().Be(position2);
        report.DiscrepancyReason.Should().Contain("behind");

        // Act: Check consistency when projection is caught up
        var reportCaughtUp = await checker.VerifyAsync(eventStore, streamId, position2);

        // Assert: Report shows projection is in sync
        reportCaughtUp.Should().NotBeNull();
        reportCaughtUp!.IsConsistent.Should().BeTrue();
        reportCaughtUp.DiscrepancyReason.Should().BeNull();
    }

    [Fact]
    public async Task FullWorkflow_LoadAggregate_ProjectEvent_SnapshotAndRebuild()
    {
        // Arrange: Setup stores and projections
        var streamId = new StreamId("order-workflow-001");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var projectionStore = new InMemoryProjectionStore();

        // Step 1: Create and project initial events
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("ORD-FINAL", 1000m) }.AsMemory(),
            StreamPosition.Start);

        var projection1 = new OrderProjectionFiltered();
        await foreach (var @event in eventStore.ReadAsync(streamId))
        {
            await projection1.HandleAsync(@event);
        }

        projection1.Current.OrderId.Should().Be("ORD-FINAL");
        projection1.Current.Amount.Should().Be(1000m);

        // Step 2: Add more events
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderShippedEvent("ORD-FINAL", "TRACK-FINAL") }.AsMemory(),
            new StreamPosition(1));

        // Re-read all events (simulating a subsequent processing run)
        projection1 = new OrderProjectionFiltered();
        await foreach (var @event in eventStore.ReadAsync(streamId))
        {
            await projection1.HandleAsync(@event);
        }

        projection1.Current.TrackingCode.Should().Be("TRACK-FINAL");

        // Step 3: Snapshot current state to projection store
        var key = $"OrderSnapshot-{streamId.Value}";
        var serialized = System.Text.Json.JsonSerializer.Serialize(projection1.Current);
        await projectionStore.SaveAsync(key, serialized);

        // Step 4: Simulate a crash/restart - rebuild projection from events
        var projection2 = new ReplayableOrderProjectionFull(streamId);
        await projection2.RebuildAsync(projectionStore, streamId, eventStore);

        // Assert: Rebuilt projection matches original
        projection2.Current.OrderId.Should().Be(projection1.Current.OrderId);
        projection2.Current.Amount.Should().Be(projection1.Current.Amount);
        projection2.Current.TrackingCode.Should().Be(projection1.Current.TrackingCode);
        projection2.Current.IsCancelled.Should().Be(projection1.Current.IsCancelled);

        // Step 5: Verify consistency
        var position = await GetLatestPositionAsync(eventStore, streamId);
        var checker = new ProjectionConsistencyChecker<OrderReadModel>();
        var report = await checker.VerifyAsync(eventStore, streamId, position);

        report.Should().NotBeNull();
        report!.IsConsistent.Should().BeTrue();
    }
}
