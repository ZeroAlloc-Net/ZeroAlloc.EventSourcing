using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.Telemetry;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

// Test helpers at namespace level so NSubstitute/Castle can proxy them
public sealed class FakeAggregate { }

public readonly record struct OrderId(Guid Value);

public sealed class InstrumentedAggregateRepositoryTests : IDisposable
{
    private static readonly string ActivitySourceName = "ZeroAlloc.EventSourcing";
    private static readonly string MeterName = "ZeroAlloc.EventSourcing";

    private readonly IAggregateRepository<FakeAggregate, OrderId> _inner;
    private readonly InstrumentedAggregateRepository<FakeAggregate, OrderId> _sut;

    // Track activities started
    private readonly List<Activity> _startedActivities = [];
    private readonly ActivityListener _activityListener;

    // Track metric measurements
    private readonly ConcurrentBag<(string Name, long Value)> _counterMeasurements = [];
    private readonly MeterListener _meterListener;

    public InstrumentedAggregateRepositoryTests()
    {
        _inner = Substitute.For<IAggregateRepository<FakeAggregate, OrderId>>();
        _sut = new InstrumentedAggregateRepository<FakeAggregate, OrderId>(_inner);

        // Set up ActivityListener to capture activities
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _startedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);

        // Set up MeterListener to capture metrics
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            _counterMeasurements.Add((instrument.Name, measurement)));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    private static readonly OrderId _orderId = new(Guid.NewGuid());
    private static readonly StreamId _streamId = new("test");

    private static ValueTask<Result<FakeAggregate, StoreError>> OkLoadResult()
        => ValueTask.FromResult(Result<FakeAggregate, StoreError>.Success(new FakeAggregate()));

    private static ValueTask<Result<AppendResult, StoreError>> OkSaveResult()
        => ValueTask.FromResult(Result<AppendResult, StoreError>.Success(new AppendResult(_streamId, StreamPosition.Start)));

    // -------------------------------------------------------------------------
    // LoadAsync tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_StartsActivity_WithCorrectName()
    {
        _inner.LoadAsync(default, default)
              .ReturnsForAnyArgs(OkLoadResult());

        await _sut.LoadAsync(_orderId);

        _startedActivities.Should().ContainSingle(a => a.OperationName == "aggregate.load");
    }

    [Fact]
    public async Task LoadAsync_SetsAggregateTypeTag()
    {
        _inner.LoadAsync(default, default)
              .ReturnsForAnyArgs(OkLoadResult());

        await _sut.LoadAsync(_orderId);

        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "aggregate.load").Subject;
        activity.GetTagItem("aggregate.type").Should().Be(nameof(FakeAggregate));
    }

    [Fact]
    public async Task LoadAsync_SetsErrorStatus_OnException()
    {
        _inner.LoadAsync(default, default)
              .ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));

        var act = async () => await _sut.LoadAsync(_orderId);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "aggregate.load").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    // -------------------------------------------------------------------------
    // SaveAsync tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_StartsActivity_WithCorrectName()
    {
        _inner.SaveAsync(default!, default, default)
              .ReturnsForAnyArgs(OkSaveResult());

        await _sut.SaveAsync(new FakeAggregate(), _orderId);

        _startedActivities.Should().ContainSingle(a => a.OperationName == "aggregate.save");
    }

    [Fact]
    public async Task SaveAsync_IncrementsCounter_OnSuccess()
    {
        _inner.SaveAsync(default!, default, default)
              .ReturnsForAnyArgs(OkSaveResult());

        await _sut.SaveAsync(new FakeAggregate(), _orderId);

        _meterListener.RecordObservableInstruments();

        _counterMeasurements
            .Should().ContainSingle(m => m.Name == "aggregate.saves_total" && m.Value == 1);
    }

    [Fact]
    public async Task SaveAsync_DoesNotIncrementCounter_OnException()
    {
        _inner.SaveAsync(default!, default, default)
              .ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));

        var act = async () => await _sut.SaveAsync(new FakeAggregate(), _orderId);
        await act.Should().ThrowAsync<InvalidOperationException>();

        _meterListener.RecordObservableInstruments();

        _counterMeasurements
            .Where(m => m.Name == "aggregate.saves_total")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_DoesNotIncrementCounter_OnResultFailure()
    {
        _inner.SaveAsync(default!, default, default)
              .ReturnsForAnyArgs(ValueTask.FromResult(
                  Result<AppendResult, StoreError>.Failure(StoreError.Unknown("store failure"))));

        await _sut.SaveAsync(new FakeAggregate(), _orderId);

        _meterListener.RecordObservableInstruments();
        _counterMeasurements
            .Where(m => m.Name == "aggregate.saves_total")
            .Should().BeEmpty();
    }

}
