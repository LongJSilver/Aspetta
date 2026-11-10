using System;
using System.Threading;

namespace Some.Restraint;

/// <summary>
///     A synchronous delegate to execute, with no access to the execution's cancellation.
/// </summary>
public delegate void DeferAction();

/// <summary>
///     A synchronous delegate to execute. It receives the execution's cancellation token:
///     honoring it lets <see cref="Defer.CancelExecuting()"/> resolve the execution as
///     canceled rather than let it run to completion.
/// </summary>
public delegate void DeferCancellableAction(CancellationToken cancel);
