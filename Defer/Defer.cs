using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Some.Restraint;

/// <summary>
///     Debounces and coalesces frequent triggers: each <see cref="Trigger" /> schedules
///     the delegate to run after the configured delay, and further triggers within that
///     window push the execution back instead of queuing new ones. Triggering is
///     synchronous: it returns a <see cref="DeferredExecution" /> (the execution the
///     call was assigned to), which faults if the delegate faults and is canceled if
///     the execution is canceled, skipped while paused, or the Defer is disposed.
///     Triggers that coalesce share the same execution and outcome, so awaiting the
///     returned value is how a caller waits for the cycle it was assigned to.
///     The worker is either a task-based asynchronous loop (<see cref="UsingTask(TaskFactory?)" />,
///     which holds no thread while idle and also accepts asynchronous delegates) or a
///     dedicated background thread (<see cref="UsingDedicatedThread" />, which runs
///     synchronous delegates only). The instance owns its worker until
///     <see cref="Dispose" /> is called; a Defer that becomes unreachable without being
///     disposed is detected by its worker, which then shuts itself down.
/// </summary>
[DebuggerDisplay("{DebuggerSummary,nq}")]
[DebuggerTypeProxy(typeof(DeferDebugView))]
[DebuggerStepThrough]
public class Defer : IDisposable
{
    // ****************** INTERNAL WORK ****************** //

    internal Defer(string? name, TimeSpan delay, Delegate? boundDelegate, Func<CancellationToken, Task> exec,
        TaskFactory taskFactory, Action<Exception>? onError = null, bool traceTriggers = false)
    {
        Core = new DeferCore(this, name, delay, boundDelegate, exec, taskFactory, onError, traceTriggers);
    }

    internal Defer(string? name, TimeSpan delay, Delegate? boundDelegate, DeferCancellableAction exec,
        Action<Exception>? onError = null, bool traceTriggers = false)
    {
        Core = new DeferCore(this, name, delay, boundDelegate, exec, onError, traceTriggers);
    }

    // ****************** PUBLIC API ****************** //

    /// <summary>
    ///     The name given via <c>Named(...)</c> on the builder, or null.
    /// </summary>
    public string? Name => Core.Name;

    /// <summary>
    ///     The configured delay applied to triggers that do not pass their own interval.
    ///     Changing it only affects cycles armed afterwards; an armed cycle keeps the
    ///     deadline it was armed with. Reading it after dispose is harmless.
    /// </summary>
    public TimeSpan Delay
    {
        get => Core.Delay;
        set => Core.Delay = value;
    }

    /// <summary>
    ///     The dedicated thread running the loop on instances built with
    ///     <see cref="UsingDedicatedThread()" />; null with the default task-based worker.
    /// </summary>
    public Thread? WorkerThread => Core.Thread;

    /// <summary>
    ///     The maximum amount of milliseconds this timer will be allowed to defer execution.
    ///     After that time span has passed, the delegate action is triggered regardless of the normal delay mechanism.
    /// </summary>
    public uint? MaximumDelaySpan
    {
        get => Core.MaximumDelaySpan;
        internal set => Core.MaximumDelaySpan = value;
    }

    /// <summary>
    ///     The maximum amount of times this timer will be allowed to defer execution.
    /// </summary>
    public uint? MaximumTriggerLimit
    {
        get => Core.MaximumTriggerLimit;
        internal set => Core.MaximumTriggerLimit = value;
    }

    /// <summary>
    ///     Which limits were configured on the builder: none, span, count, or both.
    /// </summary>
    public DeferLimit LimitType => Core.LimitType;

    /// <summary>
    ///     True while the Defer is paused through <see cref="Pause" />. Reading it
    ///     after dispose is harmless.
    /// </summary>
    public bool IsPaused
    {
        get => Core.IsPaused;
    }

    /// <summary>
    ///     Holds pending executions until <see cref="Resume(TimeSpan?)" />: a due
    ///     cycle is consumed and resolves as canceled, a still-armed cycle keeps
    ///     its deadline, and a run already executing is never disturbed.
    ///     Idempotent.
    /// </summary>
    public void Pause()
    {
        Core.Pause();
    }

    /// <summary>
    ///     Ends the pause and re-arms whatever it holds. With no interval, a cycle
    ///     still armed keeps its deadline (re-anchored on the time it had left, so
    ///     a pause is transparent to the armed deadline, and to any override
    ///     interval that armed it; a spent remaining time is a past deadline and
    ///     executes as soon as the worker can), while a pass consumed while paused
    ///     re-arms as a fresh cycle on the configured delay. With an interval,
    ///     whatever the pause holds is re-anchored at now + interval instead.
    ///     Resume never creates a run: with nothing held, it only ends the pause.
    ///     The re-arm counts as one trigger toward the cycle's trigger limit, and
    ///     cannot push the run past the burst's maximum delay span anchor. To
    ///     guarantee a run after resuming regardless of what the pause holds,
    ///     Trigger just before resuming.
    /// </summary>
    public void Resume(TimeSpan? interval = null)
    {
        Core.Resume(interval);
    }

    /// <summary>
    ///     The currently pending execution, if any. It observes the same outcomes as
    ///     the executions returned by <see cref="Trigger(TimeSpan?)" /> without
    ///     contributing a trigger, and is already over when nothing is pending.
    /// </summary>
    public DeferredExecution Pending => Core.Pending;

    internal DeferCore Core { get; }

    internal string DebuggerSummary => Core.DebuggerSummary;

    /// <summary>
    ///     Cancels the in-flight run through its token (so a delegate that honors the
    ///     token ends promptly), waits for the delegate to return, and resolves every
    ///     outstanding execution value that never began a run as canceled; it can be
    ///     called repeatedly. The in-flight run itself resolves with its own outcome:
    ///     canceled if it throws an <see cref="OperationCanceledException" />
    ///     attributable to that token (thrown on it, or without naming any token),
    ///     faulted on any other exception, completed if it returns normally.
    ///     Calling Dispose from inside the delegate itself is detected: the worker
    ///     then shuts down on its own instead of waiting on itself.
    /// </summary>
    /// <remarks>
    ///     The in-flight run is canceled through its <see cref="CancellationToken" />,
    ///     which also runs any callback the delegate registered on that token with
    ///     <c>token.Register(...)</c>. If such a callback throws, its exception
    ///     surfaces on the caller of Dispose: it is not hidden. By then the library's
    ///     own work is already done: all of its state changes happened before the
    ///     token was canceled, so a throwing callback cannot undo the dispose.
    ///     <para>
    ///         Detection of a self-dispose follows the <see cref="ExecutionContext" />, so
    ///         it also covers the delegate's await continuations and tasks spawned from
    ///         it: a Dispose from any of those returns without waiting. A Dispose issued
    ///         from inside a cancellation callback of the run's token is likewise detected
    ///         (callbacks run synchronously on the thread calling <see cref="Cancel" /> or
    ///         Dispose) and returns without waiting, because the worker may be blocked on
    ///         that very callback. A Dispose from any other context always waits for the
    ///         worker to finish, even if a racing self-dispose already ran.
    ///     </para>
    /// </remarks>
    public void Dispose()
    {
        Core.Dispose();
        // Defer has no finalizer; this call only satisfies the IDisposable
        // pattern (CA1816) so no analyzer warning fires.
        GC.SuppressFinalize(this);
    }

    // ************ PUBLIC Builder API *********** //

    // Pinned to TaskScheduler.Default so that building a Defer inside a task running
    // on a custom scheduler does not capture that scheduler via TaskScheduler.Current.
    private static readonly TaskFactory DefaultTaskFactory = new(TaskScheduler.Default);

    /// <summary>
    ///     Starts a task-based worker builder with a synchronous delegate.
    /// </summary>
    public static TaskDeferBuilder ToExecute(DeferAction act)
    {
        return UsingTask().ToExecute(act);
    }

    /// <summary>
    ///     Starts a task-based worker builder with a synchronous, cancellable delegate.
    /// </summary>
    public static TaskDeferBuilder ToExecute(DeferCancellableAction act)
    {
        return UsingTask().ToExecute(act);
    }

    /// <summary>
    ///     Starts a task-based worker builder with an asynchronous delegate.
    /// </summary>
    public static TaskDeferBuilder ToExecute(Func<Task> act)
    {
        return UsingTask().ToExecute(act);
    }

    /// <summary>
    ///     Starts a task-based worker builder with an asynchronous, cancellable delegate.
    /// </summary>
    public static TaskDeferBuilder ToExecute(Func<CancellationToken, Task> act)
    {
        return UsingTask().ToExecute(act);
    }

    /// <summary>
    ///     Returns a builder whose worker is the task-based loop. The loop is scheduled
    ///     through <paramref name="t" />, or on <see cref="TaskScheduler.Default" /> when
    ///     no factory is given.
    /// </summary>
    public static TaskDeferBuilder UsingTask(TaskFactory? t = null)
    {
        return new TaskDeferBuilder(t ?? DefaultTaskFactory);
    }

    /// <summary>
    ///     Returns a builder whose worker is a dedicated background thread. Only
    ///     synchronous delegates are accepted on this side of the builder.
    /// </summary>
    public static ThreadDeferBuilder UsingDedicatedThread()
    {
        return new ThreadDeferBuilder();
    }

    /// <summary>
    ///     Cancels the pending cycle, if any: its execution value resolves as canceled
    ///     and the delegate does not run for it. Never disturbs a run already in flight.
    /// </summary>
    public void CancelPending()
    {
        Core.CancelPending();
    }

    /// <summary>
    ///     Cancels the in-flight run through its token. A delegate that honors the token
    ///     by throwing <see cref="OperationCanceledException" /> — on that token, or
    ///     without naming any token — resolves its execution as canceled; one blaming
    ///     an unrelated token resolves it as faulted. Never disturbs a pending cycle.
    /// </summary>
    public void CancelExecuting()
    {
        Core.CancelExecuting();
    }

    /// <summary>
    ///     Cancels the pending cycle, the in-flight run, or both, per
    ///     <paramref name="type" />.
    /// </summary>
    /// <remarks>
    ///     The in-flight half is canceled through the run's
    ///     <see cref="CancellationToken" />, which also runs any callback the
    ///     delegate registered on that token. If such a callback throws, its
    ///     exception surfaces on the caller of Cancel: it is not hidden. The
    ///     pending half of the cancel runs first, before any token callback,
    ///     so a throwing callback cannot leave the pending cycle armed.
    ///     Callbacks run synchronously on the calling thread: one that blocks on
    ///     this Defer's worker can deadlock, and one that calls <see cref="Dispose" />
    ///     is detected like a self-dispose and returns without waiting.
    /// </remarks>
    public void Cancel(CancelType type = CancelType.All)
    {
        Core.Cancel(type);
    }

    /// <summary>
    ///     Schedules the delegate to run after the given interval (or the configured
    ///     <see cref="Delay" /> when null), postponing or hastening any pending cycle, and returns the
    ///     execution this call was assigned to. Coalesced triggers share it and its outcome.
    /// </summary>
    public DeferredExecution Trigger(TimeSpan? overrideInterval = null)
    {
        return Core.Trigger(overrideInterval);
    }

    /// <summary>
    ///     Schedules an immediate execution and returns it. An armed cycle is pulled to
    ///     now, and both calls resolve with that single run.
    /// </summary>
    public DeferredExecution ExecuteNow()
    {
        return Core.ExecuteNow();
    }
}
