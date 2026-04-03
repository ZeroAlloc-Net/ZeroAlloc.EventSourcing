using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Tests;

// Reuse TestEventSerializer and TestEventTypeRegistry from ReplayableProjectionTests

/// <summary>
/// Unit tests for <see cref="ProjectionConsistencyChecker{TReadModel}"/>. Verifies that the checker
/// correctly detects when a projection is up-to-date or behind the event store.
/// </summary>
public class ProjectionConsistencyCheckerTests
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
    public async Task VerifyAsync_NoProjection_ReturnsNull()
    {
        // Arrange
        var streamId = new StreamId("test-stream");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var checker = new ProjectionConsistencyChecker<OrderReadModel>();

        // Act
        var report = await checker.VerifyAsync(eventStore, streamId, null);

        // Assert
        report.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_ProjectionUpToDate_ReturnsConsistent()
    {
        // Arrange
        var streamId = new StreamId("test-stream");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var checker = new ProjectionConsistencyChecker<OrderReadModel>();

        // Append an event
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("ORD-001", 100m) }.AsMemory(),
            StreamPosition.Start);

        // Get the latest position
        var latestPosition = await GetLatestPositionAsync(eventStore, streamId);

        // Act
        var report = await checker.VerifyAsync(eventStore, streamId, latestPosition);

        // Assert
        report.Should().NotBeNull();
        report!.IsConsistent.Should().BeTrue();
        report.ProjectionCheckpoint.Should().Be(latestPosition);
        report.EventStoreLatestPosition.Should().Be(latestPosition);
        report.DiscrepancyReason.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_ProjectionBehind_ReturnsInconsistent()
    {
        // Arrange
        var streamId = new StreamId("test-stream");
        var adapter = new InMemoryEventStoreAdapter();
        var eventStore = new EventStore(adapter, new TestEventSerializer(), new TestEventTypeRegistry());
        var checker = new ProjectionConsistencyChecker<OrderReadModel>();

        // Append first event
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderPlacedEvent("ORD-001", 100m) }.AsMemory(),
            StreamPosition.Start);

        // Append second event (projection only at position 1)
        await eventStore.AppendAsync(
            streamId,
            new object[] { new OrderShippedEvent("ORD-001", "TRACK-123") }.AsMemory(),
            new StreamPosition(1));

        var projectionCheckpoint = new StreamPosition(1);

        // Act
        var report = await checker.VerifyAsync(eventStore, streamId, projectionCheckpoint);

        // Assert
        report.Should().NotBeNull();
        report!.IsConsistent.Should().BeFalse();
        report.ProjectionCheckpoint.Should().Be(projectionCheckpoint);
        report.DiscrepancyReason.Should().NotBeNullOrEmpty();
        report.DiscrepancyReason.Should().Contain("behind");
    }
}
