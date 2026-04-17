using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Telemetry;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

public sealed class InstrumentedEventStoreTests : IDisposable
{
    private static readonly string ActivitySourceName = "ZeroAlloc.EventSourcing";
    private static readonly string MeterName = "ZeroAlloc.EventSourcing";

    private readonly IEventStore _inner;
    private readonly InstrumentedEventStore _sut;
    private readonly StreamId _streamId = new("test-stream");

    // Track activities started
    private readonly List<Activity> _startedActivities = [];
    private readonly ActivityListener _activityListener;

    // Track metric measurements
    private readonly List<(string Name, long Value)> _counterMeasurements = [];
    private readonly List<(string Name, double Value)> _histogramMeasurements = [];
    private readonly MeterListener _meterListener;

    public InstrumentedEventStoreTests()
    {
        _inner = Substitute.For<IEventStore>();
        _sut = new InstrumentedEventStore(_inner);

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
        _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            _histogramMeasurements.Add((instrument.Name, measurement)));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    // Helper: empty async enumerable
    private static async IAsyncEnumerable<EventEnvelope> EmptyAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private Result<AppendResult, StoreError> OkAppendResult()
        => Result<AppendResult, StoreError>.Success(new AppendResult(_streamId, StreamPosition.Start));

    // -------------------------------------------------------------------------
    // AppendAsync tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AppendAsync_StartsActivity_WithCorrectName()
    {
        _inner.AppendAsync(default, default, default, default)
              .ReturnsForAnyArgs(ValueTask.FromResult(OkAppendResult()));

        await _sut.AppendAsync(_streamId, ReadOnlyMemory<object>.Empty, StreamPosition.Start);

        _startedActivities.Should().ContainSingle(a => a.OperationName == "event_store.append");
    }

    [Fact]
    public async Task AppendAsync_SetsStreamIdTag()
    {
        _inner.AppendAsync(default, default, default, default)
              .ReturnsForAnyArgs(ValueTask.FromResult(OkAppendResult()));

        await _sut.AppendAsync(_streamId, ReadOnlyMemory<object>.Empty, StreamPosition.Start);

        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "event_store.append").Subject;
        activity.GetTagItem("stream.id").Should().Be(_streamId.Value);
    }

    [Fact]
    public async Task AppendAsync_IncrementsCounter_OnSuccess()
    {
        _inner.AppendAsync(default, default, default, default)
              .ReturnsForAnyArgs(ValueTask.FromResult(OkAppendResult()));

        await _sut.AppendAsync(_streamId, ReadOnlyMemory<object>.Empty, StreamPosition.Start);

        _meterListener.RecordObservableInstruments();

        _counterMeasurements
            .Should().ContainSingle(m => m.Name == "event_store.appends_total" && m.Value == 1);
    }

    [Fact]
    public async Task AppendAsync_SetsErrorStatus_OnException()
    {
        _inner.AppendAsync(default, default, default, default)
              .ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));

        var act = async () => await _sut.AppendAsync(_streamId, ReadOnlyMemory<object>.Empty, StreamPosition.Start);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "event_store.append").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task AppendAsync_DoesNotIncrementCounter_OnException()
    {
        _inner.AppendAsync(default, default, default, default)
              .ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));

        var act = async () => await _sut.AppendAsync(_streamId, ReadOnlyMemory<object>.Empty, StreamPosition.Start);
        await act.Should().ThrowAsync<InvalidOperationException>();

        _meterListener.RecordObservableInstruments();

        _counterMeasurements
            .Where(m => m.Name == "event_store.appends_total")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_SetsCorrelationIdTag_WhenBaggagePresent()
    {
        _inner.AppendAsync(default, default, default, default)
              .ReturnsForAnyArgs(ValueTask.FromResult(OkAppendResult()));

        var correlationId = Guid.NewGuid().ToString();
        using var parent = new Activity("test.parent").Start();
        parent.AddBaggage("correlation.id", correlationId);
        parent.AddBaggage("causation.id", "some-causation-id");

        await _sut.AppendAsync(_streamId, ReadOnlyMemory<object>.Empty, StreamPosition.Start);

        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "event_store.append").Subject;
        activity.GetTagItem("correlation.id").Should().Be(correlationId);
        activity.GetTagItem("causation.id").Should().Be("some-causation-id");
    }

    // -------------------------------------------------------------------------
    // ReadAsync tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_StartsActivity_WithCorrectName()
    {
        _inner.ReadAsync(default, default, default)
              .ReturnsForAnyArgs(EmptyAsync());

        await foreach (var _ in _sut.ReadAsync(_streamId)) { }

        _startedActivities.Should().ContainSingle(a => a.OperationName == "event_store.read");
    }

    [Fact]
    public async Task ReadAsync_RecordsHistogram_OnSuccess()
    {
        _inner.ReadAsync(default, default, default)
              .ReturnsForAnyArgs(EmptyAsync());

        await foreach (var _ in _sut.ReadAsync(_streamId)) { }

        _meterListener.RecordObservableInstruments();

        _histogramMeasurements
            .Should().ContainSingle(m => m.Name == "event_store.read_duration_ms" && m.Value >= 0);
    }

    // -------------------------------------------------------------------------
    // SubscribeAsync tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SubscribeAsync_StartsActivity_WithCorrectName()
    {
        _inner.SubscribeAsync(default, default, default!, default)
              .ReturnsForAnyArgs(ValueTask.FromResult(Substitute.For<IEventSubscription>()));

        await _sut.SubscribeAsync(_streamId, StreamPosition.Start,
            (_, _) => ValueTask.CompletedTask);

        _startedActivities.Should().ContainSingle(a => a.OperationName == "event_store.subscribe");
    }
}
