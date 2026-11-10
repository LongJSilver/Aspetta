using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Some.Restraint;

/// <summary>
///     Blocking and <see cref="Task" />-returning forms over the <see cref="DeferredExecution" />
///     values returned by <see cref="Defer.Trigger(TimeSpan?)" />, <see cref="Defer.ExecuteNow" />
///     and <see cref="Defer.Pending" />. They are thin bridges: the async forms just return the
///     execution as a task, the blocking forms wait on it via <c>GetAwaiter().GetResult()</c>.
///     The outcomes are the execution's outcomes, unwrapped: a canceled execution surfaces as
///     <see cref="TaskCanceledException" /> and a faulting delegate propagates its original
///     exception; neither is wrapped in an <see cref="AggregateException" />.
/// </summary>
[DebuggerStepThrough]
public static class DeferExtensions
{
    /// <summary>
    ///     Triggers an execution after the given interval in milliseconds and returns the
    ///     execution this call was assigned to. Pipes into
    ///     <see cref="Defer.Trigger(TimeSpan?)" />.
    /// </summary>
    public static DeferredExecution Trigger(this Defer defer, int overrideInterval)
    {
        return defer.Trigger(TimeSpan.FromMilliseconds(overrideInterval));
    }

    /// <summary>
    ///     Triggers an execution after the given interval in milliseconds and returns the
    ///     execution this call was assigned to. Pipes into
    ///     <see cref="Defer.Trigger(TimeSpan?)" />.
    /// </summary>
    public static DeferredExecution Trigger(this Defer defer, uint overrideInterval)
    {
        return defer.Trigger(TimeSpan.FromMilliseconds(overrideInterval));
    }

    /// <summary>
    ///     Triggers an execution after the given delay (the configured delay when no
    ///     interval is given) and blocks the calling thread until it has completed,
    ///     faulted or been canceled.
    /// </summary>
    public static void TriggerAndWait(this Defer defer, TimeSpan? span = null)
    {
        defer.Trigger(span).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Triggers an execution after the given delay (the configured delay when no
    ///     interval is given) and returns it as a task.
    /// </summary>
    public static Task TriggerAndWaitAsync(this Defer defer, TimeSpan? span = null)
    {
        return defer.Trigger(span).AsTask();
    }

    /// <summary>
    ///     Triggers an immediate execution and blocks the calling thread until it has
    ///     completed, faulted or been canceled.
    /// </summary>
    public static void ExecuteNowAndWait(this Defer defer)
    {
        defer.ExecuteNow().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Triggers an immediate execution and returns it as a task.
    /// </summary>
    public static Task ExecuteNowAndWaitAsync(this Defer defer)
    {
        return defer.ExecuteNow().AsTask();
    }

    /// <summary>
    ///     Waits for the currently pending execution. Returns immediately when nothing is
    ///     pending.
    /// </summary>
    public static void WaitForNextExecution(this Defer defer)
    {
        defer.Pending.GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Returns the currently pending execution as a task: awaiting it waits for that
    ///     execution, or completes immediately when nothing is pending.
    /// </summary>
    public static Task WaitForNextExecutionAsync(this Defer defer)
    {
        return defer.Pending.AsTask();
    }
}
