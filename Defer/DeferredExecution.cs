using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Some.Restraint;

/// <summary>
///     The execution of the cycle a <see cref="Defer" /> call was assigned to: the next
///     execution to start after a trigger, the one just scheduled by
///     <see cref="Defer.ExecuteNow" />, or the one currently pending in the case of
///     <see cref="Defer.Pending" />. Triggers that coalesce into the same execution share it.
/// </summary>
/// <remarks>
///     Awaiting is optional and never part of the triggering call itself: awaiting waits
///     for the execution and propagates its outcome (completion, the delegate's fault, or
///     cancellation), while ignoring the value is the fire-and-forget form. The execution
///     can also be awaited long after it resolved: the value carries its own outcome.
///     A <c>default(DeferredExecution)</c> is not a completed execution: its
///     <see cref="IsCompleted" /> is false and awaiting it never returns.
/// </remarks>
[DebuggerDisplay("{DebuggerStatus,nq}")]
[DebuggerStepThrough]
public readonly struct DeferredExecution
{
    // There is no Task.Unstarted equivalent to Task.CompletedTask, so this stands in
    // for it: a default(DeferredExecution) never completes.
    private static readonly Task Unstarted =
        new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private readonly Task? _task;

    internal DeferredExecution(Task task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
    }

    internal string DebuggerStatus => _task?.Status.ToString() ?? "no execution";

    /// <summary>
    ///     An execution that is already over: what <see cref="Defer.Pending" /> yields
    ///     when nothing is pending.
    /// </summary>
    internal static DeferredExecution Completed => new(Task.CompletedTask);

    /// <summary>
    ///     True when the execution has completed, faulted or been canceled.
    /// </summary>
    public bool IsCompleted => _task?.IsCompleted ?? false;

    /// <summary>
    ///     The execution as a plain task, e.g. for <see cref="Task.WhenAll(Task[])" />.
    /// </summary>
    public Task AsTask()
    {
        return _task ?? Unstarted;
    }

    /// <summary>
    ///     The execution as a plain task.
    /// </summary>
    public static implicit operator Task(DeferredExecution execution)
    {
        return execution.AsTask();
    }

    /// <summary>
    ///     Awaits the execution and propagates its outcome.
    /// </summary>
    public TaskAwaiter GetAwaiter()
    {
        return AsTask().GetAwaiter();
    }

    /// <summary>
    ///     Configures how continuations of this execution are scheduled.
    /// </summary>
    public ConfiguredTaskAwaitable ConfigureAwait(bool continueOnCapturedContext)
    {
        return AsTask().ConfigureAwait(continueOnCapturedContext);
    }
}
