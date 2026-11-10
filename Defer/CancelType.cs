using System;

namespace Some.Restraint;

/// <summary>
///     Selects which part of a <see cref="Defer" /> a <see cref="Defer.Cancel(CancelType)" />
///     call cancels: the in-flight run, the pending cycle, or both.
/// </summary>
[Flags]
public enum CancelType
{
    /// <summary>
    ///     The run currently executing, canceled through its token.
    /// </summary>
    CurrentlyExecuting = 1,

    /// <summary>
    ///     The armed cycle waiting for its deadline; its execution value resolves as canceled.
    /// </summary>
    PendingExecution = 2,

    /// <summary>
    ///     Both the in-flight run and the pending cycle.
    /// </summary>
    All = CurrentlyExecuting | PendingExecution
}
