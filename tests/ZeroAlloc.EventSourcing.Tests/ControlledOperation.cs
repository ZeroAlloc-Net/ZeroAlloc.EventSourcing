using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// An operation whose completion and whose continuation the test drives separately, so a test
/// can fail it and only later let the awaiting code observe that, see #434. Completing it does not
/// run the continuation; <see cref="RunContinuation"/> does.
/// </summary>
internal sealed class ControlledOperation : IValueTaskSource, IValueTaskSource<bool>
{
    private readonly TaskCompletionSource _awaited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Action<object?>? _continuation;
    private object? _continuationState;
    private ExceptionDispatchInfo? _error;
    private bool _completed;
    private bool _result;

    /// <summary>The operation as a <see cref="ValueTask"/>.</summary>
    public ValueTask AsValueTask() => new(this, 0);

    /// <summary>The operation as a <see cref="ValueTask{TResult}"/> of <see cref="bool"/>.</summary>
    public ValueTask<bool> AsBoolValueTask() => new(this, 0);

    /// <summary>The operation as a <see cref="Task"/>, for code that awaits a task.</summary>
    public Task AsTask() => AsValueTask().AsTask();

    /// <summary>Completes once the awaiting code has registered its continuation.</summary>
    public Task Awaited => _awaited.Task;

    /// <summary>Faults the operation without running the continuation.</summary>
    public void Fail(Exception exception)
    {
        lock (_gate)
        {
            _error = ExceptionDispatchInfo.Capture(exception);
            _completed = true;
        }
    }

    /// <summary>Completes the operation with <paramref name="result"/> without running the continuation.</summary>
    public void Succeed(bool result = true)
    {
        lock (_gate)
        {
            _result = result;
            _completed = true;
        }
    }

    /// <summary>Lets the awaiting code resume and observe the outcome.</summary>
    public void RunContinuation()
    {
        Action<object?>? continuation;
        object? state;
        lock (_gate)
        {
            continuation = _continuation;
            state = _continuationState;
            _continuation = null;
        }
        continuation?.Invoke(state);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        lock (_gate)
        {
            if (!_completed) return ValueTaskSourceStatus.Pending;
            return _error is null ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Faulted;
        }
    }

    public void OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        lock (_gate)
        {
            _continuation = continuation;
            _continuationState = state;
        }
        _awaited.TrySetResult();
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);

    public bool GetResult(short token)
    {
        lock (_gate)
        {
            _error?.Throw();
            return _result;
        }
    }
}
