using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

// --- test domain model ---

/// <summary>Event that should be included in the filtered projection.</summary>
public record TestIncludedEvent(string Value);

/// <summary>Event that should be excluded from the filtered projection.</summary>
public record TestExcludedEvent(string Value);

/// <summary>Read model that accumulates values from included events.</summary>
public record TestReadModel(int EventCount, string LastIncludedValue);

/// <summary>
/// Test filtered projection that only processes TestIncludedEvent.
/// Demonstrates how to implement <see cref="FilteredProjection{TReadModel}"/>
/// by filtering on event type.
/// </summary>
public sealed class TestFilteredProjection : FilteredProjection<TestReadModel>
{
    /// <summary>Initializes the projection with an empty read model.</summary>
    public TestFilteredProjection()
    {
        Current = new TestReadModel(0, string.Empty);
    }

    /// <summary>Only include TestIncludedEvent events.</summary>
    protected override bool IncludeEvent(EventEnvelope @event)
    {
        return @event.Event is TestIncludedEvent;
    }

    /// <summary>Routes events to typed handlers and returns the updated read model.</summary>
    protected override TestReadModel Apply(TestReadModel current, EventEnvelope @event)
    {
        return @event.Event switch
        {
            TestIncludedEvent e => current with
            {
                EventCount = current.EventCount + 1,
                LastIncludedValue = e.Value
            },
            _ => current
        };
    }
}

// --- tests ---

/// <summary>
/// Unit tests for <see cref="FilteredProjection{TReadModel}"/>. Verifies that filtered projections
/// correctly filter events based on the IncludeEvent predicate and apply only included events.
/// </summary>
public class FilteredProjectionTests
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
    public async Task FilteredProjection_HandleAsyncWithIncludedEvent_AppliesChange()
    {
        // Arrange
        var projection = new TestFilteredProjection();
        var includedEvent = new TestIncludedEvent("test-value");
        var envelope = MakeEnvelope(includedEvent);

        // Act
        await projection.HandleAsync(envelope);

        // Assert
        projection.Current.Should().NotBeNull();
        projection.Current.EventCount.Should().Be(1);
        projection.Current.LastIncludedValue.Should().Be("test-value");
    }

    [Fact]
    public async Task FilteredProjection_HandleAsyncWithExcludedEvent_SkipsChange()
    {
        // Arrange
        var projection = new TestFilteredProjection();
        var excludedEvent = new TestExcludedEvent("excluded-value");
        var envelope = MakeEnvelope(excludedEvent);

        // Act
        await projection.HandleAsync(envelope);

        // Assert
        projection.Current.EventCount.Should().Be(0);
        projection.Current.LastIncludedValue.Should().Be(string.Empty);
    }

    [Fact]
    public async Task FilteredProjection_MultipleIncludedEvents_ApplySequentially()
    {
        // Arrange
        var projection = new TestFilteredProjection();
        var includedEvent1 = new TestIncludedEvent("first");
        var excludedEvent = new TestExcludedEvent("excluded");
        var includedEvent2 = new TestIncludedEvent("second");

        // Act
        await projection.HandleAsync(MakeEnvelope(includedEvent1));
        await projection.HandleAsync(MakeEnvelope(excludedEvent));
        await projection.HandleAsync(MakeEnvelope(includedEvent2));

        // Assert
        projection.Current.EventCount.Should().Be(2);
        projection.Current.LastIncludedValue.Should().Be("second");
    }
}
