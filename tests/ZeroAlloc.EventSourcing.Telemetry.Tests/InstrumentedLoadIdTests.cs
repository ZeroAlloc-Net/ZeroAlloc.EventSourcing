using AwesomeAssertions;
using NSubstitute;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

public struct CounterState : IAggregateState<CounterState>
{
    public static CounterState Initial => default;
}

public sealed class Counter : Aggregate<OrderId, CounterState>
{
    protected override CounterState ApplyEvent(CounterState state, object @event) => state;
}

/// <summary>
/// <see cref="InstrumentedAggregateRepository{TAggregate,TId}"/> returns the aggregate of the
/// repository it wraps, with the id that repository set on it. Issue #401.
/// </summary>
public sealed class InstrumentedLoadIdTests
{
    [Fact]
    public async Task LoadAsync_ThroughTheInstrumentedRepository_SetsId()
    {
        var store = new EventStore(
            new InMemoryEventStoreAdapter(), Substitute.For<IEventSerializer>(), Substitute.For<IEventTypeRegistry>());
        var inner = new AggregateRepository<Counter, OrderId>(store, static () => new Counter(), id => new StreamId($"counter-{id.Value}"));
        var sut = new InstrumentedAggregateRepository<Counter, OrderId>(inner);
        var id = new OrderId(Guid.NewGuid());

        var loaded = await sut.LoadAsync(id);

        loaded.IsSuccess.Should().BeTrue();
        using var counter = loaded.Value;
        counter.Id.Should().Be(id);
    }
}
