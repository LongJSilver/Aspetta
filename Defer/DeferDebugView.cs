using System;
using System.Threading.Tasks;

namespace Some.Restraint;

/// <summary>
///     The structured view of a <see cref="Defer" /> shown in debugger watch windows and
///     datatip expansions. Every read is lock-free: debug views are evaluated by the
///     debugger and must never block on the machinery.
/// </summary>
internal sealed class DeferDebugView
{
    // The debug view has no access to the moment the debugger stopped the process,
    // and the monotonic clock keeps running while it is frozen; time-based metrics
    // are therefore either fossils, recorded by the machinery at the events
    // themselves, or computed against this one snapshot, taken when the view was
    // created, so that every read within one expansion agrees.
    private readonly long _clockMs;
    private readonly DeferCore _core;

    public DeferDebugView(Defer defer)
    {
        _core = defer.Core;
        _clockMs = _core.ElapsedMs;
    }

    public string? Name => _core.Name;

    /// <summary>
    ///     The delegate provided to ToExecute, before any internal wrapping: its Method and
    ///     Target reveal what this Defer runs and on what instance.
    /// </summary>
    public Delegate? ExecutedDelegate => _core.BoundDelegate;

    public string Executes => _core.DebuggerDelegateName;
    public TimeSpan Delay => _core.Delay;
    public string State => _core.DebuggerState;
    public string Worker => _core.DebuggerWorker;
    public bool Paused => _core.IsPaused_NoLocksReadable;
    public long ArmedTriggers => _core.ArmedTriggers_NoLocksReadable;
    public TaskStatus? PendingCycle => _core.DebuggerPendingStatus;

    /// <summary>
    ///     The call stack of the last Trigger/ExecuteNow call, when WithTriggerTracing is
    ///     configured..
    /// </summary>
    public string? LastTriggeredBy => _core.LastTriggerStackTrace;

    /// <summary>
    ///     The call stack that opened the current (or most recent) execution cycle, when
    ///     WithTriggerTracing is configured..
    /// </summary>
    public string? CycleOpenedBy => _core.CycleOpeningStackTrace;

    /// <summary>
    ///     Elapsed-clock milliseconds at which that cycle opened. Fossilized: never changes.
    /// </summary>
    public long? OpenedAtMs => _core.CycleOpeningStackTrace != null ? _core.CycleOpeningAtMs : null;

    /// <summary>
    ///     How long ago the armed cycle opened, against the snapshot taken when this view
    ///     was created: stable while the view is open, but each new expansion reads the
    ///     clock again, since the process being frozen does not stop the monotonic clock.
    /// </summary>
    public long? PendingAgeMs => _core.DebuggerPendingStatus != null && _core.CycleOpeningStackTrace != null
        ? _clockMs - _core.CycleOpeningAtMs
        : null;

    /// <summary>
    ///     The realized debounce of the most recent run: how long the coalescing window that
    ///     produced it lasted, from the opening trigger to the pass starting. Fossilized at
    ///     pass start: never changes.
    /// </summary>
    public long? DebouncedForMs
        => _core.PassStartedAtMs != 0 && _core.CycleOpeningStackTrace != null &&
           _core.PassStartedAtMs >= _core.CycleOpeningAtMs
            ? _core.PassStartedAtMs - _core.CycleOpeningAtMs
            : null;

    public DeferLimit Limits => _core.LimitType;
}
