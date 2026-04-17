using System.Diagnostics;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Telemetry;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

public sealed class InstrumentedStreamConsumerTests : IDisposable
{
    private readonly IStreamConsumer _inner;
    private readonly InstrumentedStreamConsumer _sut;
    private readonly List<Activity> _startedActivities = [];
    private readonly ActivityListener _activityListener;

    public InstrumentedStreamConsumerTests()
    {
        _inner = Substitute.For<IStreamConsumer>();
        _sut = new InstrumentedStreamConsumer(_inner);

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ZeroAlloc.EventSourcing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _startedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose() => _activityListener.Dispose();

    [Fact]
    public async Task ConsumeAsync_StartsActivity_WithCorrectName()
    {
        _inner.ConsumerId.Returns("test-consumer");
        _inner.ConsumeAsync(Arg.Any<Func<EventEnvelope, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        await _sut.ConsumeAsync((_, _) => Task.CompletedTask);
        _startedActivities.Should().ContainSingle(a => a.OperationName == "consumer.consume");
    }

    [Fact]
    public async Task ConsumeAsync_SetsConsumerIdTag()
    {
        _inner.ConsumerId.Returns("test-consumer");
        _inner.ConsumeAsync(Arg.Any<Func<EventEnvelope, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        await _sut.ConsumeAsync((_, _) => Task.CompletedTask);
        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "consumer.consume").Subject;
        activity.GetTagItem("consumer.id").Should().Be("test-consumer");
    }

    [Fact]
    public async Task ConsumeAsync_SetsErrorStatus_OnException()
    {
        _inner.ConsumerId.Returns("test-consumer");
        _inner.ConsumeAsync(Arg.Any<Func<EventEnvelope, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("fail"));
        var act = async () => await _sut.ConsumeAsync((_, _) => Task.CompletedTask);
        await act.Should().ThrowAsync<InvalidOperationException>();
        var activity = _startedActivities.Should().ContainSingle(a => a.OperationName == "consumer.consume").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }
}
