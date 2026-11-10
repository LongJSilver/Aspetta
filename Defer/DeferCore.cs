using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Some.Restraint;

/// <summary>
///     The execution machinery behind <see cref="Defer" />. The worker either runs
///     <see cref="LoopAsync" /> as a task-based loop (holding no thread while idle, or
///     while an asynchronous execution is in flight) or <see cref="Loop" /> on a dedicated
///     thread. The worker holds a strong reference to this object only; the public
///     wrapper is tracked through <see cref="_owner" /> so that a wrapper collected
///     without being disposed still shuts the worker down.
/// </summary>
/// <remarks>
///     Every <see cref="Trigger" />, <see cref="ExecuteNow" /> and <see cref="Pending" />
///     call returns the pending execution, whose task is created at the first trigger
///     of that cycle and shared by every coalescing trigger. The task is promoted to
///     the running execution when the deadline pass begins and completed with that
///     pass's outcome (result, fault or cancellation).
/// </remarks>
[DebuggerStepThrough]
internal sealed class DeferCore : IDisposable
{
    /// <summary>
    ///     Upper bound for the worker's idle sleep. A bounded sleep keeps the cost of
    ///     an idle worker negligible while ensuring an abandoned core is noticed
    ///     within a short, fixed delay.
    /// </summary>
    private const int MAX_IDLE_SLEEP_MS = 1000;

    internal readonly object _lock;

    // ******************  ****************** //
    private readonly Func<CancellationToken, Task>? _asyncDelegate;
    internal readonly Task? _task;
    private readonly DeferCancellableAction? _syncDelegate;
    internal Thread? Thread { get; }
    // ******************  ****************** //

    private readonly Action<Exception>? _doOnError;

    /// <summary>
    /// Weak referencing allows us to detect instances that were abandoned without proper
    /// disposal, so we can clean up ourselves
    /// </summary>
    private readonly WeakReference<Defer> _owner;

    private readonly ManualResetEventSlim _signal = new(false);
    private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Stopwatch _timer;
    private readonly bool _traceTriggers;
    private bool _canceled;
    internal CancellationTokenSource? _CurrentCancellation;

    // The previous run's cancellation source, retired by the worker at the first
    // sleep after the pass end and disposed no earlier than the next one. See
    // ComputeSleep for why the disposal is deferred.
    private CancellationTokenSource? _retiredCancellation;

    // The core whose cancellation callbacks this thread is currently running:
    // cts.Cancel() executes them synchronously on the calling thread. A Dispose
    // issued from inside one of THIS core's callbacks is detected like a
    // self-dispose (see IsSelfDispose): waiting for the worker there could
    // deadlock, because that worker may be blocked on the very callback. The
    // marker is scoped to the core whose token is being canceled: an
    // unrelated Defer disposed from such a callback still waits for its own
    // worker, which is not blocked on this callback chain.
    [ThreadStatic] private static DeferCore? _cancelCallbackOwner;

    // Guards a single disposal of _signal across the worker's exit path and
    // any number of waiting external Dispose callers.
    private int _signalReleased;

    // Holds this core while its delegate is running on the worker. AsyncLocal, so the
    // value flows through the ExecutionContext into asynchronous delegates and their
    // continuations, on whatever thread they resume. The value is the executing core
    // itself: a context that merely flowed out of another Defer's delegate (spawned
    // tasks, continuations) is never mistaken for this worker; only a context this
    // core's own worker produced is. Note the flip side: tasks spawned FROM this
    // core's delegate also carry the value, so a Dispose issued from such a child is
    // treated as a self-dispose and returns without waiting.
    private static readonly AsyncLocal<DeferCore?> OnWorker = new();

    // ****************** STATE ****************** //

    // Kept as ticks so the read side is a lock-free Volatile.Read: debug views
    // must never block on the machinery, and a TimeSpan read is not guaranteed
    // atomic on 32-bit runtimes. The setter still takes the lock for its
    // disposed check.
    private long _delayTicks;
    private long _executionLimit = long.MaxValue;
    private long _nextExecution = long.MaxValue;

    // The tasks of the pending and the in-flight execution, guarded by _lock.
    // Every trigger of the same cycle shares the pending task, created at the
    // first trigger of that cycle, and the task is promoted to running when the
    // deadline pass begins, so it is always completed by the pass it was bound
    // to, never by an unrelated resolution.
    private TaskCompletionSource<object?>? _pendingExecution;
    private TaskCompletionSource<object?>? _runningExecution;

    internal volatile bool _destroyed;
    private bool _paused;
    private bool _pendingWhilePaused;
    private long _armedTriggers;

    internal long CycleOpeningAtMs;
    internal string? CycleOpeningStackTrace;

    // Trigger provenance, populated only when WithTriggerTracing was
    // configured, in DEBUG and RELEASE builds alike. Read by the debug
    // views; never under a lock.
    internal string? LastTriggerStackTrace;

    // Elapsed-clock time at which the last due pass began (whether it executed or
    // was skipped): written on every worker pass, so that the debug views can show
    // fossilized, snapshot-stable metrics.
    internal long PassStartedAtMs;

    // ****************** INTERNAL WORK ****************** //

    internal DeferCore(Defer owner, string? name, TimeSpan delay, Delegate? boundDelegate,
        Func<CancellationToken, Task> @delegate, TaskFactory taskFactory, Action<Exception>? onError = null,
        bool traceTriggers = false)
    {
        _traceTriggers = traceTriggers;
        _owner = new WeakReference<Defer>(owner);
        Name = name;
        _delayTicks = delay.Ticks;
        BoundDelegate = boundDelegate;
        _asyncDelegate = @delegate;
        _doOnError = onError;
        _timer = Stopwatch.StartNew();
        _lock = new object();

        // The loop only occupies a thread until its first await: while idle, or
        // while an asynchronous execution is in flight, no thread is held at all.
        _task = taskFactory.StartNew(LoopAsync).Unwrap();
        Thread = null;
    }

    internal DeferCore(Defer owner, string? name, TimeSpan delay, Delegate? boundDelegate, DeferCancellableAction @delegate,
        Action<Exception>? onError = null, bool traceTriggers = false)
    {
        _traceTriggers = traceTriggers;
        _owner = new WeakReference<Defer>(owner);
        Name = name;
        _delayTicks = delay.Ticks;
        BoundDelegate = boundDelegate;
        _syncDelegate = @delegate;
        _doOnError = onError;
        _timer = Stopwatch.StartNew();
        _lock = new object();

        _task = null;

        Thread = new Thread(Loop)
        {
            IsBackground = true
        };
        Thread.Start();
    }

    // ****************** API (through Defer) ****************** //

    public string? Name { get; }

    public TimeSpan Delay
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _delayTicks));
        set
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                Volatile.Write(ref _delayTicks, value.Ticks);
            }
        }
    }

    // -1 encodes "not configured". Kept as a long so reads are lock-free volatile
    // reads (debug views must never block on the machinery) and publication to the
    // worker is safe even though the worker starts before Build() applies limits.
    private long _maximumDelaySpan = -1;
    private long _maximumTriggerLimit = -1;

    /// <summary>
    ///     The maximum amount of milliseconds this timer will be allowed to defer execution.
    ///     After that time span has passed, the delegate action is triggered regardless of the normal delay mechanism.
    /// </summary>
    public uint? MaximumDelaySpan
    {
        get
        {
            var v = Volatile.Read(ref _maximumDelaySpan);
            return v < 0 ? (uint?)null : (uint)v;
        }
        set => Volatile.Write(ref _maximumDelaySpan, value.HasValue ? value.Value : -1L);
    }

    /// <summary>
    ///     The maximum amount of times this timer will be allowed to defer execution.
    /// </summary>
    public uint? MaximumTriggerLimit
    {
        get
        {
            var v = Volatile.Read(ref _maximumTriggerLimit);
            return v < 0 ? (uint?)null : (uint)v;
        }
        set => Volatile.Write(ref _maximumTriggerLimit, value.HasValue ? value.Value : -1L);
    }

    public DeferLimit LimitType
    {
        get
        {
            var result = DeferLimit.NoLimit;
            if (MaximumDelaySpan.HasValue) result |= DeferLimit.Span;

            if (MaximumTriggerLimit.HasValue) result |= DeferLimit.Count;
            return result;
        }
    }

    /// <summary>
    ///     True while the Defer is paused: pending executions are held (a due cycle
    ///     is consumed and resolves as canceled, a still-armed cycle keeps its
    ///     deadline) and an in-flight run is never disturbed. Reading it after
    ///     dispose is harmless; <see cref="Pause" /> and <see cref="Resume" />
    ///     throw, like every mutating member.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>
    ///     Holds pending executions until <see cref="Resume" />. Idempotent, and
    ///     never disturbs a run already executing.
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _paused = true;
        }
    }

    /// <summary>
    ///     Ends the pause and re-arms whatever it holds, if it holds anything:
    ///     a cycle still armed keeps its deadline (the resume re-anchors on the
    ///     time the cycle had left, so a pause is transparent to the armed
    ///     deadline, and to any override interval that armed it; a spent
    ///     remaining time is a past deadline and executes as soon as the worker
    ///     can), while a pass consumed while paused re-arms as a fresh cycle on
    ///     the configured delay. With an interval, whatever the pause holds is
    ///     re-anchored at now + interval instead. Resume never creates a run:
    ///     with nothing held it only ends the pause. The re-arm counts as one
    ///     trigger toward the cycle's trigger limit, and cannot push the run
    ///     past the burst's maximum delay span anchor. To guarantee a run after
    ///     resuming regardless of what the pause holds, trigger just before
    ///     resuming: Trigger unconditionally arms or re-anchors, and Resume then
    ///     holds nothing hostile.
    /// </summary>
    public void Resume(TimeSpan? interval = null)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var wasPaused = _paused;
            _paused = false;
            // Re-arm only if something is actually pending: either a trigger
            // still armed, or one that was consumed by the worker while paused.
            // An unpaused Resume is an idempotent no-op: any armed cycle is
            // already running on its own deadline.
            if (!wasPaused || (_armedTriggers == 0 && !_pendingWhilePaused))
                return;

            _pendingWhilePaused = false;
            // An explicit interval re-anchors whatever the pause holds at
            // now + interval. Without one, an armed cycle keeps its deadline
            // (re-anchored on the time it had left), and a consumed pass re-arms
            // fresh on the configured delay: its _nextExecution is the "no
            // deadline" sentinel, and must not leak into the remaining-time
            // formula.
            TimeSpan? anchor = interval;
            if (anchor is null && _armedTriggers > 0)
                anchor = TimeSpan.FromMilliseconds(_nextExecution - _timer.ElapsedMilliseconds);
            Trigger(anchor);
        }
    }

    /// <summary>
    ///     The currently pending execution, if any. It observes the same outcomes as
    ///     the executions returned by <see cref="Trigger(TimeSpan?)" /> without
    ///     contributing a trigger, and is already over when nothing is pending.
    /// </summary>
    public DeferredExecution Pending
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                TaskCompletionSource<object?>? pending = _pendingExecution;
                return pending != null
                    ? new DeferredExecution(pending.Task)
                    : DeferredExecution.Completed;
            }
        }
    }

    // ****************** DEBUG VIEWS ****************** //
    // Lock-free by design: debug views are evaluated by the debugger and must
    // never block on the machinery. Reads may be slightly incoherent snapshots.

    internal long ElapsedMs => _timer.ElapsedMilliseconds;

    /// <summary>
    ///     A one-line description of this Defer, for the debugger display.
    /// </summary>
    internal string DebuggerSummary
        => $"{Name ?? "(unnamed)"}: {DebuggerDelegateName} after {Delay.TotalMilliseconds:0} ms - {DebuggerState}";

    internal string DebuggerDelegateName
    {
        get
        {
            MethodInfo? method = BoundDelegate?.Method;
            return method != null ? $"{method.DeclaringType?.Name}.{method.Name}" : "<not configured>";
        }
    }

    internal Delegate? BoundDelegate { get; }

    internal string DebuggerState
        => _destroyed ? "disposed"
            : _runningExecution != null ? "executing"
            : _pendingExecution != null ? $"armed ({_armedTriggers} triggers)"
            : "idle";

    internal string DebuggerWorker => _asyncDelegate != null ? "task worker" : "dedicated thread";

    // Unconventional name on purpose: this is the no-lock read access to the
    // IsPaused state (_paused) for the debugger views, which must never block on
    // _lock. The public IsPaused property takes the lock because its setter
    // mutates and re-arms; this internal accessor is a plain read of the field.
    internal bool IsPaused_NoLocksReadable => _paused;

    // Same rule as IsPaused_NoLocksReadable: no-lock read access to the armed
    // trigger count (_armedTriggers) for the debugger views.
    internal long ArmedTriggers_NoLocksReadable => _armedTriggers;

    internal TaskStatus? DebuggerPendingStatus => _pendingExecution?.Task.Status;

    public void Dispose()
    {
        bool self;
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            self = IsSelfDispose();
            if (!_destroyed)
            {
                _destroyed = true;
                // Take the in-flight run's source to cancel it below: a delegate that
                // honors the token can then end promptly instead of running to completion
                // while Dispose waits for it.
                cts = _CurrentCancellation;
                WakeWorker();
            }
            // Otherwise a racing Dispose (possibly a self-dispose from the delegate)
            // already did the teardown; this caller still waits below, unless it is
            // the worker itself or a cancellation callback.
        }

        // Outside the lock: token registrations are user callbacks and may throw or
        // block. The source may have been retired at the pass end if the run
        // finished meanwhile, and disposed once the grace period has passed. A
        // throwing callback must not skip the wait below, so its exception is
        // captured and rethrown after the worker has been joined; it still surfaces
        // on this caller, as documented.
        Exception? cancelError = null;
        if (cts != null)
        {
            var previousOwner = _cancelCallbackOwner;
            _cancelCallbackOwner = this;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { cancelError = ex; }
            finally { _cancelCallbackOwner = previousOwner; }
        }

        if (!self)
        {
            // Every caller that is neither the worker itself nor a cancellation
            // callback waits for the worker to finish, even when a racing
            // self-dispose already marked the core disposed.
            try
            {
                if (Thread != null)
                {
                    Thread.Join();
                }
                else if (_task != null)
                {
                    _task.Wait();
                    _task.Dispose();
                }
            }
            finally
            {
                ReleaseSignal();
            }
        }

        if (cancelError != null)
            ExceptionDispatchInfo.Capture(cancelError).Throw();
    }

    /// <summary>
    ///     Disposes <see cref="_signal" /> exactly once, whichever caller gets there
    ///     first: the worker on its way out, or a waiting external Dispose.
    /// </summary>
    private void ReleaseSignal()
    {
        if (Interlocked.Exchange(ref _signalReleased, 1) == 0)
            _signal.Dispose();
    }

    /// <summary>
    ///     Wakes the worker immediately. A core has exactly one worker — the
    ///     dedicated-thread loop waits on <see cref="_signal" />, the task loop on
    ///     <see cref="_wake" /> — so setting both covers either kind; the one nobody
    ///     waits on is a cheap no-op. Use it when state changed under
    ///     <see cref="_lock" /> in a way that must not wait out the worker's current
    ///     sleep (a new or pulled deadline, a cancel, a dispose): the worker then
    ///     re-evaluates promptly instead of at the deadline or the next idle poll.
    ///     Must be called while holding <see cref="_lock" />.
    /// </summary>
    private void WakeWorker()
    {
        _signal.Set();
        _wake.TrySetResult(true);
    }

    /// <summary>
    ///     True when the caller is this core's worker itself: inside the delegate
    ///     (the <see cref="OnWorker" /> marker flows into async continuations), or
    ///     on the dedicated worker thread. Must be called while holding
    ///     <see cref="_lock" />.
    /// </summary>
    private bool IsOnWorker()
    {
        if (ReferenceEquals(OnWorker.Value, this)) return true;
        return Thread != null && Thread.CurrentThread == Thread;
    }

    /// <summary>
    ///     The self-dispose rule: true when a <see cref="Dispose" /> issued on the
    ///     calling thread must not wait for the worker, because waiting could
    ///     deadlock. That is the case when the caller is the worker itself
    ///     (<see cref="IsOnWorker" /> — a delegate disposing its own Defer), or when
    ///     the caller is a cancellation callback that <c>cts.Cancel()</c> is executing
    ///     synchronously on the canceling thread for THIS core's token, while this
    ///     core's worker may be blocked on that very callback. Such a Dispose still
    ///     performs the full state teardown under <see cref="_lock" /> (marking
    ///     <see cref="_destroyed" />, waking the worker, canceling the in-flight
    ///     token) but returns without waiting; the worker then notices
    ///     <see cref="_destroyed" /> and finishes its current pass and exits on its
    ///     own. A Dispose issued from a cancellation callback of any other core
    ///     always waits for that other worker, which is not blocked on this
    ///     callback. Every other caller always waits for the worker to finish.
    ///     Must be called while holding <see cref="_lock" />.
    /// </summary>
    private bool IsSelfDispose()
    {
        return IsOnWorker() || ReferenceEquals(_cancelCallbackOwner, this);
    }

    public void CancelPending()
    {
        Cancel(CancelType.PendingExecution);
    }

    public void CancelExecuting()
    {
        Cancel(CancelType.CurrentlyExecuting);
    }

    public void Cancel(CancelType type = CancelType.All)
    {
        ThrowIfDisposed();
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            // Re-check: Dispose may have completed between the outer check and the
            // lock, and its _signal is disposed outside the lock: touching it here
            // would race that disposal.
            ThrowIfDisposed();
            if (type.HasFlag(CancelType.CurrentlyExecuting))
                cts = _CurrentCancellation;

            if (type.HasFlag(CancelType.PendingExecution))
            {
                // Forget a pass a pause consumed, so unpausing does not
                // re-arm it, even if a new trigger armed in the meantime.
                _pendingWhilePaused = false;
                if (_armedTriggers > 0)
                {
                    // Consume the pending execution now and cancel its task,
                    // instead of leaving it waiting for a pass that will never run.
                    _canceled = true;
                    _armedTriggers = 0;
                    _nextExecution = long.MaxValue;
                    _executionLimit = long.MaxValue;
                    Complete(ref _pendingExecution, true, null);
                    WakeWorker();
                }
            }
        }

        // Cancel outside the lock: token registrations are user callbacks and may
        // throw or block on the lock themselves. The pending half above has already
        // run, so a throwing callback cannot skip it. The source may have been
        // retired at the pass end if the in-flight run finished in the meantime
        // (and disposed once the grace period has passed): a cancel that raced the
        // pass end still reaches its registrations while the grace holds, and the
        // ObjectDisposedException afterwards makes it a no-op. The owner marker
        // lets a Dispose issued from inside one of those callbacks detect that it
        // must not wait for this core's worker (see Dispose); a Dispose reaching
        // any other core from such a callback is still joined elsewhere.
        if (cts != null)
        {
            var previousOwner = _cancelCallbackOwner;
            _cancelCallbackOwner = this;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { _cancelCallbackOwner = previousOwner; }
        }
    }

    /// <summary>
    ///     Schedules an execution after the given delay, extending any pending one,
    ///     and returns that execution. Coalesced triggers share it and its outcome.
    /// </summary>
    public DeferredExecution Trigger(TimeSpan? overrideInterval = null)
    {
        ThrowIfDisposed();
        return Arm(overrideInterval);
    }

    /// <summary>
    ///     Schedules an immediate execution and returns it, with the same outcome
    ///     semantics as <see cref="Trigger(TimeSpan?)" />.
    /// </summary>
    public DeferredExecution ExecuteNow()
    {
        ThrowIfDisposed();
        DeferredExecution execution;
        bool openedCycle;
        long now;
        lock (_lock)
        {
            ThrowIfDisposed();
            _canceled = false;
            _armedTriggers += 1;
            _nextExecution = now = _timer.ElapsedMilliseconds;
            WakeWorker();
            execution = GetOrCreatePendingExecution(out openedCycle);
        }

        if (_traceTriggers) RecordTriggerProvenance(openedCycle, now);
        return execution;
    }

    private void ThrowIfDisposed()
    {
        if (_destroyed)
            throw new ObjectDisposedException(nameof(Defer));
    }

    /// <summary>
    ///     Reports a delegate fault to the configured handler. The handler is invoked
    ///     defensively: if it throws, the worker must survive (on the dedicated thread
    ///     an escaping exception would kill the process; on the task worker it would
    ///     fault the loop and leave every outstanding execution unresolved). The
    ///     execution still faults with the delegate's original exception.
    /// </summary>
    private void ReportError(Exception ex)
    {
        try
        {
            _doOnError?.Invoke(ex);
        }
        catch
        {
            // never let an error handler take down the worker
        }
    }

    /// <summary>
    ///     Arms the next execution and returns it. The task is registered under the
    ///     same lock as the arming, so it can only ever be completed by the execution
    ///     this call scheduled or coalesced into.
    /// </summary>
    private DeferredExecution Arm(TimeSpan? overrideInterval)
    {
        DeferredExecution execution;
        bool areWeOpeningThisCycle;
        long now;
        lock (_lock)
        {
            ThrowIfDisposed();
            _canceled = false;

            var interval = overrideInterval ?? TimeSpan.FromTicks(_delayTicks);
            _armedTriggers += 1;
            now = _timer.ElapsedMilliseconds;

            _nextExecution = now + (long)interval.TotalMilliseconds;

            if (MaximumTriggerLimit.HasValue && _armedTriggers >= MaximumTriggerLimit)
                //'we have a count limit
                _nextExecution = now;

            if (MaximumDelaySpan.HasValue)
            {
                //'we have a span limit
                _executionLimit = Math.Min(now + MaximumDelaySpan.Value, _executionLimit);
                _nextExecution = Math.Min(_executionLimit, _nextExecution);
            }

            WakeWorker();
            execution = GetOrCreatePendingExecution(out areWeOpeningThisCycle);
        }

        // Outside the lock: capturing a stack trace is not a "short internal lock".
        if (_traceTriggers) RecordTriggerProvenance(areWeOpeningThisCycle, now);
        return execution;
    }

    /// <summary>
    ///     Returns the shared execution of the pending cycle, creating its task at the
    ///     first trigger of that cycle so that coalescing triggers never allocate.
    ///     <paramref name="isThisANewCycle" /> reports whether this call created it, i.e.
    ///     whether the cycle was not already open. Must be called while holding
    ///     <see cref="_lock" />.
    /// </summary>
    private DeferredExecution GetOrCreatePendingExecution(out bool isThisANewCycle)
    {
        if (_pendingExecution is null)
        {
            _pendingExecution = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            isThisANewCycle = true;
        }
        else
        {
            isThisANewCycle = false;
        }
        return new DeferredExecution(_pendingExecution.Task);
    }

    /// <summary>
    ///     Completes the given tcs's task with its outcome: <paramref name="canceled" />
    ///     cancels it, <paramref name="fault" /> faults it with the delegate's exception,
    ///     and neither completes it successfully.
    /// </summary>
    private static void Complete(ref TaskCompletionSource<object?>? tcs, bool canceled, Exception? fault)
    {
        TaskCompletionSource<object?>? tcs_localReference = tcs;
        tcs = null;
        if (tcs_localReference == null) return;
        if (canceled) tcs_localReference.TrySetCanceled();
        else if (fault != null) tcs_localReference.TrySetException(fault);
        else tcs_localReference.TrySetResult(null);
    }

    /// <summary>
    ///     Checks the deadline and, when it is due, promotes the pending execution to the
    ///     running one. Returns true when the delegate should run, in which case
    ///     <paramref name="token" /> is its cancellation token. A due deadline that cannot
    ///     run (canceled or paused) resolves the execution as canceled here.
    /// </summary>
    internal bool TryBeginExecution(out CancellationToken token)
    {
        lock (_lock)
        {
            // A Dispose that won the lock before this pass must not let it start:
            // an execution still pending at dispose resolves as canceled.
            if (_destroyed)
            {
                token = default;
                Complete(ref _pendingExecution, true, null);
                return false;
            }

            var now = _timer.ElapsedMilliseconds;
            if (now < _nextExecution)
            {
                token = default;
                return false;
            }

            PassStartedAtMs = now;

            // should execute?
            // The _canceled half of this check is currently redundant, and kept
            // on purpose: the only writers that leave _canceled true (Cancel's
            // pending half, with something armed) also push _nextExecution to
            // the "no deadline" sentinel, and every writer that makes a deadline
            // reachable again — Arm through Trigger, ExecuteNow, the resume
            // re-arm — resets the flag before arming. So reaching this line with
            // _canceled true is impossible today. That proof spans Cancel, Arm
            // and ExecuteNow, none of them local to this method: keep the check,
            // and it turns load-bearing again under any future edit that breaks
            // one of those invariants, instead of silently running a canceled pass.
            var execute = !_canceled & !_paused;
            // The pending task is bound to this pass from now on.
            _runningExecution = _pendingExecution;
            _pendingExecution = null;
            if (execute) _CurrentCancellation = new CancellationTokenSource();
            // A trigger that reached its deadline while paused is consumed:
            // remember it so that unpausing re-arms it.
            if (_paused) _pendingWhilePaused = true;
            // setup long sleep time
            _canceled = false;
            _nextExecution = long.MaxValue;
            _executionLimit = long.MaxValue;
            _armedTriggers = 0;
            token = _CurrentCancellation?.Token ?? default;
            if (!execute) Complete(ref _runningExecution, true, null);
            return execute;
        }
    }

    /// <summary>
    ///     Resolves the running execution with the pass outcome. Must be called after the
    ///     delegate has returned, whatever the outcome.
    /// </summary>
    private void ResolveExecution(bool canceledByToken, Exception? fault)
    {
        lock (_lock)
        {
            Complete(ref _runningExecution, canceledByToken, fault);
        }
    }

    /// <summary>
    ///     Disposes the execution's cancellation source and computes the bounded idle
    ///     sleep. Must be called while holding <see cref="_lock" />.
    /// </summary>
    private long ComputeSleep()
    {
        // Dispose the source retired on the previous loop iteration, not the one
        // ending now: an external Cancel/Dispose may have read
        // _CurrentCancellation under the lock while the run was in flight and
        // still be inside cts.Cancel() on it, running user callbacks — disposing a
        // source mid-notify violates the CTS contract and can skip a callback.
        // The grace is best effort, not a guarantee: usually the next iteration
        // is a whole pass or the bounded idle sleep away, far beyond the short
        // callbacks the documentation demands — but a pass already due (e.g. a
        // trigger from inside the delegate) can compress it to milliseconds.
        // On the current BCL a disposal mid-notify is anyway benign; retiring is
        // the belt, the grace the braces.
        _retiredCancellation?.Dispose();
        _retiredCancellation = _CurrentCancellation;
        _CurrentCancellation = null;
        var now = _timer.ElapsedMilliseconds;
        var sleepFor = Math.Min(int.MaxValue, _nextExecution - now);
        // Keep the idle sleep bounded so an abandoned core is noticed soon.
        return Math.Min(sleepFor, MAX_IDLE_SLEEP_MS);
    }

    private void Loop()
    {
        while (!_destroyed)
        {
            // The public wrapper is gone and nobody can dispose us:
            // the owner dropped this Defer without Dispose. Stop the worker
            // instead of leaking it forever.
            if (IsAbandoned()) break;

            if (TryBeginExecution(out CancellationToken token))
            {
                var canceledByToken = false;
                Exception? fault = null;
                OnWorker.Value = this;
                try
                {
                    _syncDelegate!(token);
                }
                catch (OperationCanceledException oce)
                    when (token.IsCancellationRequested &&
                          (oce.CancellationToken == token || !oce.CancellationToken.CanBeCanceled))
                {
                    // the delegate honored the cancellation of this run: this run
                    // was canceled, not failed, and the error handler is not
                    // invoked. The OCE must be attributable to this run: thrown on
                    // the run's token, or without naming any token at all while
                    // the run is canceled. One blaming an unrelated token falls
                    // through to the fault below: the outcome is what the
                    // delegate actually did.
                    canceledByToken = true;
                }
                catch (Exception ex)
                {
                    fault = ex;
                    ReportError(ex);
                }
                finally
                {
                    OnWorker.Value = null;
                }

                ResolveExecution(canceledByToken, fault);
            }

            long sleepFor;
            _signal.Reset();
            lock (_lock)
            {
                sleepFor = ComputeSleep();
            }

            // A Dispose (or self-dispose) that ran while this pass was in flight
            // had its signal wake wiped by the Reset above; without a deadline
            // nothing else would wake the wait before the idle sleep ran out.
            // Notice it now and leave promptly instead of waiting out the idle sleep.
            if (_destroyed) break;

            _signal.Wait(TimeSpan.FromMilliseconds(Math.Max(sleepFor, 1)));
        }

        // we are shutting down
        ShutdownResolve();
        // Release the signal whichever way the worker stops — external Dispose (its
        // caller releases it too; ReleaseSignal is exactly-once), self-dispose, or
        // abandonment, where nobody else will.
        ReleaseSignal();
    }

    private async Task LoopAsync()
    {
        while (!_destroyed)
        {
            // The public wrapper is gone and nobody can dispose us:
            // the owner dropped this Defer without Dispose. Stop the worker
            // instead of leaking it forever.
            if (IsAbandoned()) break;

            if (TryBeginExecution(out CancellationToken token))
            {
                var canceledByToken = false;
                Exception? fault = null;
                OnWorker.Value = this;
                try
                {
                    await _asyncDelegate!(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                    when (token.IsCancellationRequested &&
                          (oce.CancellationToken == token || !oce.CancellationToken.CanBeCanceled))
                {
                    // the delegate honored the cancellation of this run: this run
                    // was canceled, not failed, and the error handler is not
                    // invoked. The OCE must be attributable to this run: thrown on
                    // the run's token, or without naming any token at all while
                    // the run is canceled. One blaming an unrelated token falls
                    // through to the fault below: the outcome is what the
                    // delegate actually did.
                    canceledByToken = true;
                }
                catch (Exception ex)
                {
                    fault = ex;
                    ReportError(ex);
                }
                finally
                {
                    OnWorker.Value = null;
                }

                ResolveExecution(canceledByToken, fault);
            }

            Task wakeTask;
            long sleepFor;
            lock (_lock)
            {
                wakeTask = _wake.Task;
                // Consume a wake that already happened while an execution was in
                // flight; a fresh one is installed for wakes after this point, which
                // complete it and are therefore observed by this sleep's await.
                if (wakeTask.IsCompleted)
                    _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sleepFor = ComputeSleep();
            }

            if (!wakeTask.IsCompleted)
                await Task.WhenAny(Task.Delay((int)Math.Max(sleepFor, 1)), wakeTask).ConfigureAwait(false);
        }

        // we are shutting down
        ShutdownResolve();
        // Release the signal whichever way the worker stops — external Dispose (its
        // caller releases it too; ReleaseSignal is exactly-once), self-dispose, or
        // abandonment, where nobody else will.
        ReleaseSignal();
    }

    /// <summary>
    ///     Cancels every execution that could still be outstanding when the worker stops.
    /// </summary>
    private void ShutdownResolve()
    {
        lock (_lock)
        {
            _retiredCancellation?.Dispose();
            _retiredCancellation = null;
            Complete(ref _runningExecution, true, null);
            Complete(ref _pendingExecution, true, null);
        }
    }

    /// <summary>
    ///     True when the public wrapper has been collected. Kept in its own method so
    ///     that the wrapper reference read by TryGetTarget only ever lives in this
    ///     short-lived frame: a wrapper reference lingering on the worker's stack
    ///     across its blocking wait would pin the wrapper and defeat the check.
    /// </summary>
    private bool IsAbandoned()
    {
        return !_owner.TryGetTarget(out _);
    }

    /// <summary>
    ///     Records the provenance of a trigger or cycle opening onto the debug
    ///     fields, when the Defer was built with tracing configured. Compiled
    ///     into DEBUG and RELEASE builds alike: tracing is a runtime choice of
    ///     the instance, not a build of the library.
    /// </summary>
    internal void RecordTriggerProvenance(bool openedCycle, long atMs)
    {
        var stack = Environment.StackTrace;
        LastTriggerStackTrace = stack;
        if (openedCycle)
        {
            CycleOpeningStackTrace = stack;
            CycleOpeningAtMs = atMs;
        }
    }
}
