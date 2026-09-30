using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.EventSourcing.Internal;

/// <summary>
/// Tells a failure the work's own stop caused from a failure that happened before the stop was
/// requested. A loop runs its operations through <see cref="Track(ValueTask)"/> with
/// <see cref="Token"/>, and a stop is requested through <see cref="RequestStopAsync"/> or an
/// outer token. A failed operation counts as caused by the stop when the stop was requested while
/// it was in flight: after it was invoked and before it had completed.
/// </summary>
/// <remarks>
/// <para>
/// Checking the token when the exception is caught is not enough. An operation can fail on its
/// own, and the stop can be requested before the loop resumes to observe that failure; the token
/// is then cancelled although the failure came first, see #434. So the stop itself looks at the
/// operation in flight, under the same lock the loop uses to publish and retire it: whether that
/// operation had already completed is a fact at that moment, whatever runs later.
/// </para>
/// <para>
/// An operation that completes synchronously is retired when the loop checks it. If the stop was
/// requested before then, the operation may have seen the cancelled token while it ran, so its
/// failure counts as caused by the stop. An operation invoked after the stop counts the same way.
/// </para>
/// <para>
/// No allocation per operation: the operation is kept in a field, and the awaiter is a struct.
/// The loop runs one operation at a time.
/// </para>
/// <para>
/// This file is compiled into each assembly that uses it, so no assembly depends on another's
/// internals.
/// </para>
/// </remarks>
internal sealed class StopScope : IDisposable
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _outer;

    // All guarded by _gate.
    private bool _stopRequested;
    private InFlight _inFlight;
    private ValueTask _operation;
    private ValueTask<bool> _boolOperation;
    private bool _inFlightAtStop;
    private bool _verdict;
    private Exception? _causedByStop;

    private enum InFlight { None, Operation, BoolOperation }

    /// <summary>A scope stopped only through <see cref="RequestStopAsync"/>.</summary>
    public StopScope()
    {
    }

    /// <summary>A scope also stopped when <paramref name="outer"/> is cancelled.</summary>
    public StopScope(CancellationToken outer)
    {
        // Runs inside outer's Cancel, before this scope's token is cancelled, so the operation in
        // flight is looked at before it can see the cancellation. Runs at once if outer is
        // already cancelled.
        _outer = outer.Register(static state => ((StopScope)state!).RequestStop(), this);
    }

    /// <summary>The token to pass to every operation; cancelled only by a stop.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Whether a stop was requested.</summary>
    public bool IsStopRequested
    {
        get { lock (_gate) return _stopRequested; }
    }

    /// <summary>
    /// Requests the stop: records whether an operation was in flight, then cancels
    /// <see cref="Token"/>. Later calls do nothing.
    /// </summary>
    public Task RequestStopAsync() => MarkStopped() ? _cts.CancelAsync() : Task.CompletedTask;

    private void RequestStop()
    {
        if (MarkStopped()) _cts.Cancel();
    }

    private bool MarkStopped()
    {
        lock (_gate)
        {
            if (_stopRequested) return false;
            _stopRequested = true;
            _inFlightAtStop = _inFlight switch
            {
                InFlight.Operation => !_operation.IsCompleted,
                InFlight.BoolOperation => !_boolOperation.IsCompleted,
                _ => false,
            };
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> was caused by the stop: it is the failure of an
    /// operation that was in flight when the stop was requested, or it is a cancellation of
    /// <see cref="Token"/>, which only the stop cancels.
    /// </summary>
    public bool IsCausedByStop(Exception exception)
    {
        if (exception is OperationCanceledException oce && oce.CancellationToken == _cts.Token)
            return true;
        lock (_gate) return ReferenceEquals(exception, _causedByStop);
    }

    /// <summary>
    /// Records an exception an operation threw synchronously, before it returned a task. It
    /// counts as caused by the stop when the stop was requested before it was thrown.
    /// </summary>
    public void ObserveThrown(Exception exception)
    {
        lock (_gate)
        {
            if (_stopRequested) _causedByStop = exception;
        }
    }

    /// <summary>Awaits <paramref name="operation"/> and records whether a failure of it was caused by the stop.</summary>
    public OperationAwaiter Track(ValueTask operation) => new(this, operation);

    /// <summary>Awaits <paramref name="operation"/> and records whether a failure of it was caused by the stop.</summary>
    public BoolOperationAwaiter Track(ValueTask<bool> operation) => new(this, operation);

    /// <summary>Awaits <paramref name="operation"/> and records whether a failure of it was caused by the stop.</summary>
    public OperationAwaiter Track(Task operation) => new(this, new ValueTask(operation));

    // Called from the awaiter's IsCompleted, once per operation, before the loop waits on it.
    private bool Begin(InFlight kind, ValueTask operation, ValueTask<bool> boolOperation)
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                // Invoked after the stop, or the stop came while it ran synchronously.
                _verdict = true;
                return true;
            }

            var completed = kind == InFlight.Operation ? operation.IsCompleted : boolOperation.IsCompleted;
            if (completed)
            {
                _verdict = false;
                return true;
            }

            _inFlight = kind;
            _operation = operation;
            _boolOperation = boolOperation;
            _inFlightAtStop = false;
            return false;
        }
    }

    // Called from the awaiter's GetResult, before the operation's result is read: once read, a
    // pooled operation may be reused, so the stop must no longer look at it.
    private void End()
    {
        lock (_gate)
        {
            if (_inFlight == InFlight.None) return; // completed synchronously; Begin set _verdict
            _verdict = _stopRequested && _inFlightAtStop;
            _inFlight = InFlight.None;
            _operation = default;
            _boolOperation = default;
        }
    }

    private bool Record(Exception exception)
    {
        lock (_gate)
        {
            if (_verdict) _causedByStop = exception;
        }
        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _outer.Dispose();
        _cts.Dispose();
    }

    /// <summary>The awaiter <see cref="Track(ValueTask)"/> returns.</summary>
    public readonly struct OperationAwaiter : ICriticalNotifyCompletion
    {
        private readonly StopScope _scope;
        private readonly ValueTask _operation;

        internal OperationAwaiter(StopScope scope, ValueTask operation)
        {
            _scope = scope;
            _operation = operation;
        }

        public OperationAwaiter GetAwaiter() => this;

        public bool IsCompleted => _scope.Begin(InFlight.Operation, _operation, default);

        public void OnCompleted(Action continuation) =>
            _operation.ConfigureAwait(false).GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) =>
            _operation.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);

        public void GetResult()
        {
            _scope.End();
            try
            {
                _operation.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (_scope.Record(ex))
            {
                // Unreachable: Record returns false, so the exception propagates unchanged.
            }
        }
    }

    /// <summary>The awaiter <see cref="Track(ValueTask{bool})"/> returns.</summary>
    public readonly struct BoolOperationAwaiter : ICriticalNotifyCompletion
    {
        private readonly StopScope _scope;
        private readonly ValueTask<bool> _operation;

        internal BoolOperationAwaiter(StopScope scope, ValueTask<bool> operation)
        {
            _scope = scope;
            _operation = operation;
        }

        public BoolOperationAwaiter GetAwaiter() => this;

        public bool IsCompleted => _scope.Begin(InFlight.BoolOperation, default, _operation);

        public void OnCompleted(Action continuation) =>
            _operation.ConfigureAwait(false).GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) =>
            _operation.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);

        public bool GetResult()
        {
            _scope.End();
            try
            {
                return _operation.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (_scope.Record(ex))
            {
                // Unreachable: Record returns false, so the exception propagates unchanged.
                throw;
            }
        }
    }
}
