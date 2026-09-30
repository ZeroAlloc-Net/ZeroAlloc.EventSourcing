using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Examples.Testing;

/// <summary>
/// This example demonstrates how to test aggregates thoroughly.
///
/// Testing aggregates is straightforward because:
/// 1. No database needed - the in-memory event store is enough
/// 2. No mocks needed - just create aggregates directly
/// 3. Command methods are synchronous; only saving and loading are async
/// 4. State transitions are pure functions - same input always produces same output
///
/// Tests only use the public API: commands, State, Version and OriginalVersion, and the
/// repository. To see which events a command raised, save the aggregate through a repository
/// over the in-memory store and read the stream back.
///
/// This file is compiled and run by the test suite.
/// </summary>

// Test domain model (simplified Order for testing)
public readonly record struct TestOrderId(Guid Value);

public partial struct TestOrderState : IAggregateState<TestOrderState>
{
    public static TestOrderState Initial => default;

    public bool IsPlaced { get; private set; }
    public bool IsConfirmed { get; private set; }
    public decimal Total { get; private set; }

    internal TestOrderState Apply(OrderPlacedEvent e) =>
        this with { IsPlaced = true, Total = e.Total };

    internal TestOrderState Apply(OrderConfirmedEvent _) =>
        this with { IsConfirmed = true };
}

public record OrderPlacedEvent(decimal Total);
public record OrderConfirmedEvent;

// The source generator adds ApplyEvent and TestOrderEventTypeRegistry to this partial class.
public sealed partial class TestOrder : Aggregate<TestOrderId, TestOrderState>
{
    public void Place(decimal total)
    {
        if (total <= 0)
            throw new ArgumentException("Total must be positive");

        Raise(new OrderPlacedEvent(total));
    }

    public void Confirm()
    {
        if (!State.IsPlaced)
            throw new InvalidOperationException("Cannot confirm unplaced order");

        if (State.IsConfirmed)
            throw new InvalidOperationException("Order already confirmed");

        Raise(new OrderConfirmedEvent());
    }
}

/// <summary>
/// A repository over a fresh in-memory event store, plus a helper that reads back the events
/// a save appended. Create one per test.
/// </summary>
public sealed class TestOrderHarness
{
    public TestOrderHarness()
    {
        EventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new TestOrderEventTypeRegistry());
        Repository = new AggregateRepository<TestOrder, TestOrderId>(EventStore, () => new TestOrder(), StreamIdFor);
    }

    public IEventStore EventStore { get; }

    public IAggregateRepository<TestOrder, TestOrderId> Repository { get; }

    public static StreamId StreamIdFor(TestOrderId id) => new($"order-{id.Value}");

    /// <summary>Saves the order and returns the events that save appended, in order.</summary>
    public async Task<List<object>> SaveAndReadNewEventsAsync(TestOrder order, TestOrderId id)
    {
        var from = order.OriginalVersion;
        var saved = await Repository.SaveAsync(order, id);
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.ToString() : null);

        var events = new List<object>();
        await foreach (var envelope in EventStore.ReadAsync(StreamIdFor(id), from))
            events.Add(envelope.Event);
        return events;
    }

    // A reflection-based JSON serializer is fine in tests.
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => JsonSerializer.Deserialize(payload.Span, eventType)!;
    }
}

// ===== UNIT TESTS =====

public class AggregateTestingExamples
{
    // ===== Test 1: Happy Path =====

    /// <summary>
    /// Test the happy path: place an order.
    /// Saving through the repository shows exactly which events the command raised.
    /// </summary>
    [Fact]
    public async Task Place_RaisesOrderPlacedEvent()
    {
        // Arrange
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        using var order = new TestOrder();

        // Act
        order.Place(1000m);

        // Assert
        Assert.True(order.State.IsPlaced);
        Assert.Equal(1000m, order.State.Total);

        var events = await harness.SaveAndReadNewEventsAsync(order, orderId);
        var placed = Assert.IsType<OrderPlacedEvent>(Assert.Single(events));
        Assert.Equal(1000m, placed.Total);
    }

    /// <summary>
    /// Test confirming an order after placing it.
    /// Only the events raised since the last save are appended by the next save.
    /// </summary>
    [Fact]
    public async Task Confirm_AfterPlace_RaisesOrderConfirmedEvent()
    {
        // Arrange
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        using var order = new TestOrder();
        order.Place(1000m);
        await harness.SaveAndReadNewEventsAsync(order, orderId);  // Persist the earlier event

        // Act
        order.Confirm();

        // Assert
        Assert.True(order.State.IsConfirmed);

        var events = await harness.SaveAndReadNewEventsAsync(order, orderId);
        Assert.IsType<OrderConfirmedEvent>(Assert.Single(events));
    }

    // ===== Test 2: Error Cases =====

    /// <summary>
    /// Test that placing with invalid total throws, and raises nothing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Place_WithInvalidTotal_Throws(decimal total)
    {
        // Arrange
        using var order = new TestOrder();

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => order.Place(total));
        Assert.Contains("positive", ex.Message);

        // No event was raised: Version only moves when an event is applied
        Assert.Equal(StreamPosition.Start, order.Version);
    }

    /// <summary>
    /// Test that confirming an unplaced order throws.
    /// </summary>
    [Fact]
    public void Confirm_WhenNotPlaced_Throws()
    {
        // Arrange
        using var order = new TestOrder();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Contains("unplaced", ex.Message);
    }

    // ===== Test 3: State Machine =====

    /// <summary>
    /// Test that order follows state machine correctly.
    /// </summary>
    [Fact]
    public void Aggregate_FollowsCorrectStateTransitions()
    {
        // Arrange
        using var order = new TestOrder();

        // Initial state
        Assert.False(order.State.IsPlaced);
        Assert.False(order.State.IsConfirmed);

        // After place
        order.Place(1000m);
        Assert.True(order.State.IsPlaced);
        Assert.False(order.State.IsConfirmed);

        // After confirm
        order.Confirm();
        Assert.True(order.State.IsPlaced);
        Assert.True(order.State.IsConfirmed);
    }

    // ===== Test 4: Event Replay =====

    /// <summary>
    /// Test that aggregate state is correctly reconstructed from events.
    /// Loading through the repository replays the stream onto a fresh aggregate.
    /// </summary>
    [Fact]
    public async Task Load_ReconstructsState()
    {
        // Arrange: Create an order and save its events
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        using var originalOrder = new TestOrder();
        originalOrder.Place(1500m);
        originalOrder.Confirm();
        await harness.SaveAndReadNewEventsAsync(originalOrder, orderId);

        // Act: Reconstruct the aggregate from its stream
        var loaded = await harness.Repository.LoadAsync(orderId);
        using var reconstructedOrder = loaded.Value;

        // Assert: Reconstructed state matches original
        Assert.Equal(originalOrder.State.IsPlaced, reconstructedOrder.State.IsPlaced);
        Assert.Equal(originalOrder.State.IsConfirmed, reconstructedOrder.State.IsConfirmed);
        Assert.Equal(originalOrder.State.Total, reconstructedOrder.State.Total);
    }

    /// <summary>
    /// Build a test's starting point from events, "given these events happened":
    /// append them to the in-memory store, then load the aggregate.
    /// </summary>
    [Fact]
    public async Task Confirm_GivenPlacedAndConfirmed_Throws()
    {
        // Arrange: the history is written straight to the store
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        await harness.EventStore.AppendAsync(
            TestOrderHarness.StreamIdFor(orderId),
            new object[] { new OrderPlacedEvent(1000m), new OrderConfirmedEvent() },
            StreamPosition.Start);

        var loaded = await harness.Repository.LoadAsync(orderId);
        using var order = loaded.Value;

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Contains("already confirmed", ex.Message);
    }

    // ===== Test 5: Event Sourcing Properties =====

    /// <summary>
    /// Test that aggregate version is tracked correctly.
    /// Version is important for optimistic locking.
    /// </summary>
    [Fact]
    public void Version_IncrementsWithEachEvent()
    {
        // Arrange
        using var order = new TestOrder();
        var startVersion = order.Version;

        // Act
        order.Place(1000m);
        var afterPlace = order.Version;

        order.Confirm();
        var afterConfirm = order.Version;

        // Assert
        Assert.Equal(0, startVersion.Value);
        Assert.Equal(1, afterPlace.Value);
        Assert.Equal(2, afterConfirm.Value);

        // Nothing is persisted yet, so OriginalVersion has not moved
        Assert.Equal(StreamPosition.Start, order.OriginalVersion);
    }

    /// <summary>
    /// Test that OriginalVersion is set when loading from store.
    /// The repository uses it as the expected version on the next save.
    /// </summary>
    [Fact]
    public async Task OriginalVersion_IsSetAfterLoadingFromStore()
    {
        // Arrange: one event already in the store
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        using (var placed = new TestOrder())
        {
            placed.Place(1000m);
            await harness.SaveAndReadNewEventsAsync(placed, orderId);
        }

        // Act: load, then raise one more event
        var loaded = await harness.Repository.LoadAsync(orderId);
        using var order = loaded.Value;
        order.Confirm();

        // Assert
        Assert.Equal(new StreamPosition(1), order.OriginalVersion);
        Assert.Equal(new StreamPosition(2), order.Version);
    }

    /// <summary>
    /// Two copies of the same order loaded at the same version: the first save wins,
    /// the second fails with a CONFLICT error instead of overwriting.
    /// </summary>
    [Fact]
    public async Task ConcurrentSave_SecondWriterGetsConflict()
    {
        // Arrange
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        using (var placed = new TestOrder())
        {
            placed.Place(1000m);
            await harness.SaveAndReadNewEventsAsync(placed, orderId);
        }

        using var first = (await harness.Repository.LoadAsync(orderId)).Value;
        using var second = (await harness.Repository.LoadAsync(orderId)).Value;
        first.Confirm();
        second.Confirm();

        // Act
        var firstSave = await harness.Repository.SaveAsync(first, orderId);
        var secondSave = await harness.Repository.SaveAsync(second, orderId);

        // Assert
        Assert.True(firstSave.IsSuccess);
        Assert.True(secondSave.IsFailure);
        Assert.Equal("CONFLICT", secondSave.Error.Code);
    }

    // ===== Test 6: Multiple Aggregates =====

    /// <summary>
    /// Test multiple aggregates in sequence.
    /// Each aggregate maintains its own state independently.
    /// </summary>
    [Fact]
    public void MultipleAggregates_AreIndependent()
    {
        // Arrange
        using var order1 = new TestOrder();
        using var order2 = new TestOrder();

        // Act
        order1.Place(1000m);
        order2.Place(2000m);

        order1.Confirm();
        // order2 is not confirmed

        // Assert
        Assert.True(order1.State.IsConfirmed);
        Assert.False(order2.State.IsConfirmed);
        Assert.Equal(1000m, order1.State.Total);
        Assert.Equal(2000m, order2.State.Total);
    }

    // ===== Test 7: Edge Cases =====

    /// <summary>
    /// Test that confirming twice throws.
    /// </summary>
    [Fact]
    public void Confirm_TwiceInARow_Throws()
    {
        // Arrange
        using var order = new TestOrder();
        order.Place(1000m);
        order.Confirm();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Contains("already confirmed", ex.Message);
    }

    /// <summary>
    /// Saving an aggregate with no new events appends nothing and succeeds.
    /// </summary>
    [Fact]
    public async Task Save_WithNoNewEvents_AppendsNothing()
    {
        // Arrange
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());
        using var order = new TestOrder();
        order.Place(1000m);
        await harness.SaveAndReadNewEventsAsync(order, orderId);

        // Act
        var events = await harness.SaveAndReadNewEventsAsync(order, orderId);

        // Assert
        Assert.Empty(events);
        Assert.Equal(order.Version, order.OriginalVersion);
    }

    // ===== Integration Test: Save and Load =====

    /// <summary>
    /// Integration test: save through the repository, then load it back.
    /// This tests the complete aggregate lifecycle.
    /// </summary>
    [Fact]
    public async Task AggregateLifecycle_SaveAndLoad()
    {
        // Setup: repository over an in-memory event store
        var harness = new TestOrderHarness();
        var orderId = new TestOrderId(Guid.NewGuid());

        // Step 1: Create and save order
        using var order1 = new TestOrder();
        order1.Place(1500m);
        order1.Confirm();

        var saved = await harness.Repository.SaveAsync(order1, orderId);
        Assert.True(saved.IsSuccess);
        Assert.Equal(new StreamPosition(2), saved.Value.NextExpectedVersion);

        // Step 2: Load order from event store
        var loaded = await harness.Repository.LoadAsync(orderId);
        Assert.True(loaded.IsSuccess);
        using var order2 = loaded.Value;

        // Step 3: Verify loaded state matches original
        Assert.Equal(order1.State.IsPlaced, order2.State.IsPlaced);
        Assert.Equal(order1.State.IsConfirmed, order2.State.IsConfirmed);
        Assert.Equal(order1.State.Total, order2.State.Total);
        Assert.Equal(order1.Version, order2.Version);
    }

    // ===== Performance Test =====

    /// <summary>
    /// Test that aggregate operations are fast.
    /// </summary>
    [Fact]
    public void Place_IsPerformant()
    {
        // Arrange
        var iterations = 1000;

        // Act
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using var o = new TestOrder();
            o.Place(1000m);
        }
        stopwatch.Stop();

        // Assert: generous bound so the test is not flaky on a busy CI machine
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Too slow: {stopwatch.ElapsedMilliseconds}ms for {iterations} iterations");
    }
}
