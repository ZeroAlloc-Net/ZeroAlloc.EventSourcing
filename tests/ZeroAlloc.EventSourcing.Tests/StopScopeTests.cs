using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Internal;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// <see cref="StopScope"/> counts a failure as caused by the stop only when the operation was in
/// flight when the stop was requested. Each test drives the awaiting by hand, so the order of
/// failure, stop and observation is fixed. See #434.
/// </summary>
public sealed class StopScopeTests
{
    [Fact]
    public async Task ValueTask_FailedBeforeTheStop_ObservedAfterIt_IsNotCausedByTheStop()
    {
        using var scope = new StopScope();
        var operation = new ControlledOperation();
        var awaiter = scope.Track(operation.AsValueTask());
        awaiter.IsCompleted.Should().BeFalse();

        operation.Fail(new InvalidOperationException("failed first"));
        await scope.RequestStopAsync();

        var thrown = Record(awaiter.GetResult);
        thrown.Should().BeOfType<InvalidOperationException>();
        scope.IsCausedByStop(thrown!).Should().BeFalse();
    }

    [Fact]
    public async Task ValueTask_InFlightAtTheStop_ThenFailed_IsCausedByTheStop()
    {
        using var scope = new StopScope();
        var operation = new ControlledOperation();
        var awaiter = scope.Track(operation.AsValueTask());
        awaiter.IsCompleted.Should().BeFalse();

        await scope.RequestStopAsync();
        operation.Fail(new InvalidOperationException("aborted by the stop"));

        var thrown = Record(awaiter.GetResult);
        scope.IsCausedByStop(thrown!).Should().BeTrue();
    }

    [Fact]
    public async Task BoolValueTask_FailedBeforeTheStop_ObservedAfterIt_IsNotCausedByTheStop()
    {
        using var scope = new StopScope();
        var operation = new ControlledOperation();
        var awaiter = scope.Track(operation.AsBoolValueTask());
        awaiter.IsCompleted.Should().BeFalse();

        operation.Fail(new InvalidOperationException("failed first"));
        await scope.RequestStopAsync();

        var thrown = Record(() => awaiter.GetResult());
        scope.IsCausedByStop(thrown!).Should().BeFalse();
    }

    [Fact]
    public async Task BoolValueTask_InFlightAtTheStop_ThenFailed_IsCausedByTheStop()
    {
        using var scope = new StopScope();
        var operation = new ControlledOperation();
        var awaiter = scope.Track(operation.AsBoolValueTask());
        awaiter.IsCompleted.Should().BeFalse();

        await scope.RequestStopAsync();
        operation.Fail(new InvalidOperationException("aborted by the stop"));

        var thrown = Record(() => awaiter.GetResult());
        scope.IsCausedByStop(thrown!).Should().BeTrue();
    }

    [Fact]
    public async Task Task_FailedBeforeTheStop_ObservedAfterIt_IsNotCausedByTheStop()
    {
        using var scope = new StopScope();
        var handler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = scope.Track(handler.Task);
        awaiter.IsCompleted.Should().BeFalse();

        handler.SetException(new InvalidOperationException("failed first"));
        await scope.RequestStopAsync();

        var thrown = Record(awaiter.GetResult);
        scope.IsCausedByStop(thrown!).Should().BeFalse();
    }

    [Fact]
    public async Task Task_InFlightAtTheStop_ThenFailed_IsCausedByTheStop()
    {
        using var scope = new StopScope();
        var handler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = scope.Track(handler.Task);
        awaiter.IsCompleted.Should().BeFalse();

        await scope.RequestStopAsync();
        handler.SetException(new InvalidOperationException("aborted by the stop"));

        var thrown = Record(awaiter.GetResult);
        scope.IsCausedByStop(thrown!).Should().BeTrue();
    }

    [Fact]
    public void FailedSynchronously_BeforeTheStop_IsNotCausedByTheStop()
    {
        using var scope = new StopScope();
        var awaiter = scope.Track(ValueTask.FromException(new InvalidOperationException("failed")));
        awaiter.IsCompleted.Should().BeTrue();

        var thrown = Record(awaiter.GetResult);
        scope.IsCausedByStop(thrown!).Should().BeFalse();
    }

    // The operation may have seen the cancelled token while it ran synchronously, so a failure
    // that is first checked after the stop counts as caused by it.
    [Fact]
    public async Task InvokedOrCheckedAfterTheStop_IsCausedByTheStop()
    {
        using var scope = new StopScope();
        await scope.RequestStopAsync();

        var awaiter = scope.Track(ValueTask.FromException(new InvalidOperationException("after the stop")));
        awaiter.IsCompleted.Should().BeTrue();

        var thrown = Record(awaiter.GetResult);
        scope.IsCausedByStop(thrown!).Should().BeTrue();
    }

    [Fact]
    public async Task ThrownSynchronously_CountsByWhetherTheStopCameFirst()
    {
        using var scope = new StopScope();
        var before = new InvalidOperationException("before");
        scope.ObserveThrown(before);
        await scope.RequestStopAsync();
        var after = new InvalidOperationException("after");
        scope.ObserveThrown(after);

        scope.IsCausedByStop(before).Should().BeFalse();
        scope.IsCausedByStop(after).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationOfTheScopeToken_IsCausedByTheStop_AndOfAnotherTokenIsNot()
    {
        using var scope = new StopScope();
        using var other = new CancellationTokenSource();
        await other.CancelAsync();
        await scope.RequestStopAsync();

        scope.IsCausedByStop(new OperationCanceledException(scope.Token)).Should().BeTrue();
        scope.IsCausedByStop(new OperationCanceledException(other.Token)).Should().BeFalse();
    }

    // The outer token's cancellation looks at the operation in flight before the scope's own token
    // is cancelled, so an operation that observes only the scope's token cannot finish in between.
    [Fact]
    public async Task OuterToken_RequestsTheStop_BeforeTheScopeTokenIsCancelled()
    {
        using var outer = new CancellationTokenSource();
        using var scope = new StopScope(outer.Token);
        var operation = new ControlledOperation();
        var awaiter = scope.Track(operation.AsValueTask());
        awaiter.IsCompleted.Should().BeFalse();
        using var registration = scope.Token.Register(
            () => operation.Fail(new InvalidOperationException("aborted by the stop")));

        await outer.CancelAsync();

        scope.IsStopRequested.Should().BeTrue();
        scope.Token.IsCancellationRequested.Should().BeTrue();
        var thrown = Record(awaiter.GetResult);
        scope.IsCausedByStop(thrown!).Should().BeTrue();
    }

    [Fact]
    public void OuterTokenAlreadyCancelled_StopsAtOnce()
    {
        using var outer = new CancellationTokenSource();
        outer.Cancel();

        using var scope = new StopScope(outer.Token);

        scope.IsStopRequested.Should().BeTrue();
        scope.Token.IsCancellationRequested.Should().BeTrue();
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
