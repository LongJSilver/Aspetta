using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Some.Restraint;

/// <summary>
///     Common configuration for building a <see cref="Defer" />. The worker is chosen by
///     the entry points on <see cref="Defer" />: <see cref="Defer.UsingTask(TaskFactory?)" />
///     yields a <see cref="TaskDeferBuilder" />, whose delegates may be asynchronous, while
///     <see cref="Defer.UsingDedicatedThread" /> yields a <see cref="ThreadDeferBuilder" />,
///     which only accepts synchronous delegates.
/// </summary>
[DebuggerStepThrough]
public abstract class DeferBuilder<TSelf> where TSelf : DeferBuilder<TSelf>
{
    private protected TimeSpan? _delay;
    private protected uint? _maximumDelaySpan;
    private protected uint? _maximumTriggerCount;
    private protected string? _name;
    private protected Action<Exception>? _onError;
    private protected bool _traceTriggers;

    private protected DeferBuilder()
    {
    }

    private protected abstract TSelf Self { get; }

    /// <summary>
    ///     Gives the Defer a name, shown in the debugger display and readable as
    ///     <see cref="Defer.Name" />.
    /// </summary>
    public TSelf Named(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        _name = name;
        return Self;
    }

    /// <summary>
    ///     Sets the delay each trigger waits before the execution runs, in milliseconds.
    ///     Zero values make every trigger execute as soon as the worker can.
    /// </summary>
    public TSelf ForAtLeast(uint delayMilliseconds)
    {
        _delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        return Self;
    }

    /// <summary>
    ///     Sets the delay each trigger waits before the execution runs. Zero or negative
    ///     values make every trigger execute as soon as the worker can.
    /// </summary>
    public TSelf ForAtLeast(TimeSpan delay)
    {
        _delay = delay;
        return Self;
    }

    /// <summary>
    ///     Caps the total time a cycle may be deferred: the execution happens at most
    ///     this many milliseconds after the first trigger of the cycle. A span of 0
    ///     turns the Defer into a pass-through.
    /// </summary>
    public TSelf WithMaximumDelay(uint maximumDelaySpan)
    {
        _maximumDelaySpan = maximumDelaySpan;
        return Self;
    }

    /// <summary>
    ///     Caps how many triggers a cycle may coalesce: the Nth trigger executes
    ///     immediately. A limit of 1 turns the Defer into a pass-through.
    /// </summary>
    public TSelf WithMaximumTriggerLimit(uint maximumTriggerCount)
    {
        _maximumTriggerCount = maximumTriggerCount;
        return Self;
    }

    /// <summary>
    ///     Registers a handler invoked with the delegate's exception when a run faults.
    ///     The handler is invoked defensively: if it throws, the worker survives and the
    ///     execution still faults with the delegate's original exception.
    /// </summary>
    public TSelf WithErrorHandler(Action<Exception> onError)
    {
        if (onError is null) throw new ArgumentNullException(nameof(onError));
        _onError = onError;
        return Self;
    }

    /// <summary>
    ///     Records trigger provenance: the call stacks of every
    ///     Trigger/ExecuteNow call, and of the call that opened the current cycle, are
    ///     captured and surfaced on the debugger view of the Defer. Breaking inside the
    ///     delegate then answers "who pulled this trigger". Compiled into DEBUG and
    ///     RELEASE builds alike, so it works in the Release-built NuGet package too:
    ///     the provenance is recorded by the instances that configured it, whenever
    ///     they were built — including a defer created on the spot (e.g. from the
    ///     debugger's immediate window) on a process you just attached to, without
    ///     recompiling the faulty runtime.
    ///     The stack captures are paid only by traced defers; instances built
    ///     without this option record nothing and pay one flag check per trigger.
    /// </summary>
    public TSelf WithTriggerTracing()
    {
        _traceTriggers = true;
        return Self;
    }

    private protected void RequireDelay()
    {
        if (_delay == null)
            throw new InvalidOperationException("Call ForAtLeast(...) to configure a delay before Build().");
    }

    private protected void ApplyLimits(Defer defer)
    {
        defer.MaximumDelaySpan = _maximumDelaySpan;
        defer.MaximumTriggerLimit = _maximumTriggerCount;
    }
}

/// <summary>
///     Builds a <see cref="Defer" /> whose worker is a task-based asynchronous loop: it
///     holds no thread while idle, and its delegate may be asynchronous. Synchronous
///     delegates are wrapped as already-completed tasks, at no cost.
/// </summary>
[DebuggerStepThrough]
public sealed class TaskDeferBuilder : DeferBuilder<TaskDeferBuilder>
{
    private readonly TaskFactory _taskFactory;
    private Delegate? _bound;
    private Func<CancellationToken, Task>? _exec;

    internal TaskDeferBuilder(TaskFactory taskFactory)
    {
        _taskFactory = taskFactory;
    }

    private protected override TaskDeferBuilder Self => this;

    /// <summary>
    ///     Registers a synchronous delegate, wrapped as an already-completed task.
    /// </summary>
    public TaskDeferBuilder ToExecute(DeferAction exec)
    {
        if (exec is null) throw new ArgumentNullException(nameof(exec));
        DeferDelegateGuard.RejectAsyncVoid(exec);
        _bound = exec;
        _exec = _ =>
        {
            exec();
            return Task.CompletedTask;
        };
        return this;
    }

    /// <summary>
    ///     Registers a synchronous delegate that receives the execution's cancellation token.
    /// </summary>
    public TaskDeferBuilder ToExecute(DeferCancellableAction exec)
    {
        if (exec is null) throw new ArgumentNullException(nameof(exec));
        DeferDelegateGuard.RejectAsyncVoid(exec);
        _bound = exec;
        _exec = ct =>
        {
            exec(ct);
            return Task.CompletedTask;
        };
        return this;
    }

    /// <summary>
    ///     Registers an asynchronous delegate; while it runs, no thread is held at all.
    /// </summary>
    public TaskDeferBuilder ToExecute(Func<Task> exec)
    {
        if (exec is null) throw new ArgumentNullException(nameof(exec));
        _bound = exec;
        _exec = _ => exec();
        return this;
    }

    /// <summary>
    ///     Registers an asynchronous delegate that receives the execution's cancellation token.
    /// </summary>
    public TaskDeferBuilder ToExecute(Func<CancellationToken, Task> exec)
    {
        if (exec is null) throw new ArgumentNullException(nameof(exec));
        _bound = exec;
        _exec = exec;
        return this;
    }

    /// <summary>
    ///     Builds the <see cref="Defer" /> and starts its worker. Each call builds an
    ///     independent instance with its own worker.
    /// </summary>
    public Defer Build()
    {
        if (_exec == null)
            throw new InvalidOperationException("Execution action must be specified.");
        RequireDelay();
        var defer = new Defer(_name, _delay!.Value, _bound, _exec, _taskFactory, _onError, _traceTriggers);
        ApplyLimits(defer);
        return defer;
    }
}

/// <summary>
///     Builds a <see cref="Defer" /> whose worker is a dedicated background thread running
///     the delegate synchronously. Asynchronous delegates are not part of this builder's
///     API: a dedicated thread that must await asynchronously has nothing to dedicate
///     itself to; of course, an async lambda will quietly compile as DeferAction (async void),
///     so the ToExecute(DeferAction exec) API is more of a guidance; but an async lambda
///     compiled into a DeferAction is still rejected when the Defer is built.
/// </summary>
[DebuggerStepThrough]
public sealed class ThreadDeferBuilder : DeferBuilder<ThreadDeferBuilder>
{
    private Delegate? _bound;
    private DeferCancellableAction? _exec;

    internal ThreadDeferBuilder()
    {
    }

    private protected override ThreadDeferBuilder Self => this;

    /// <summary>
    ///     Registers a synchronous delegate, run on the dedicated thread.
    /// </summary>
    public ThreadDeferBuilder ToExecute(DeferAction exec)
    {
        if (exec is null) throw new ArgumentNullException(nameof(exec));
        DeferDelegateGuard.RejectAsyncVoid(exec);
        _bound = exec;
        _exec = _ => exec();
        return this;
    }

    /// <summary>
    ///     Registers a synchronous delegate that receives the execution's cancellation
    ///     token, run on the dedicated thread.
    /// </summary>
    public ThreadDeferBuilder ToExecute(DeferCancellableAction exec)
    {
        if (exec is null) throw new ArgumentNullException(nameof(exec));
        DeferDelegateGuard.RejectAsyncVoid(exec);
        _bound = exec;
        _exec = exec;
        return this;
    }

    /// <summary>
    ///     Builds the <see cref="Defer" /> and starts its dedicated thread. Each call
    ///     builds an independent instance with its own worker.
    /// </summary>
    public Defer Build()
    {
        if (_exec == null)
            throw new InvalidOperationException("Execution action must be specified.");
        RequireDelay();
        var defer = new Defer(_name, _delay!.Value, _bound, _exec, _onError, _traceTriggers);
        ApplyLimits(defer);
        return defer;
    }
}

/// <summary>
///     An async lambda converts silently to a void-returning delegate (async void),
///     which the compiler happily binds to the synchronous overloads: the type system
///     cannot express "any delegate except an asynchronous one". This check closes
///     that gap at build time with a clear failure instead of a worker that awaits
///     behind its delegate's back.
/// </summary>
[DebuggerStepThrough]
internal static class DeferDelegateGuard
{
    internal static void RejectAsyncVoid(Delegate exec)
    {
        if (exec.Method.IsDefined(typeof(AsyncStateMachineAttribute), false))
            throw new InvalidOperationException(
                "Asynchronous delegates must be registered as Func<Task> or Func<CancellationToken, Task> "
                + "with Defer.UsingTask(); the dedicated-thread worker only accepts synchronous delegates.");
    }
}
