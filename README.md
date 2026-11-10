# Aspetta

[![NuGet](https://img.shields.io/nuget/v/Aspetta.svg)](https://www.nuget.org/packages/Aspetta)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)

**Aspetta** is a minimal .NET micro-library for debouncing and coalescing frequent triggers. It targets .NET Standard 2.0 and has no runtime dependencies. It is developed and tested on Linux under .NET 10.

Available on NuGet:

```sh
dotnet add package Aspetta
```

---

## Features

- Delay execution after a trigger
- Coalesce rapid triggers into a single execution
- Synchronous or asynchronous delegates
- Every trigger returns an awaitable value for the execution it was assigned to
- Optional max delay span or trigger count limits
- Pause, and cancel pending or executing runs
- Task-based worker that holds no thread while idle, or a dedicated thread
- Debugger-friendly: readable hover text and optional trigger tracing

## But Why?

Aspetta is useful when:

- You need to debounce noisy events (file watchers, UI events, sensors)
- You want to batch changes and apply them all at once
- You need throttle-like control with clear limits

For comparison: what most APIs call debounce is `Throttle` in Rx.NET. Aspetta does the same for a single hot call rather than an observable stream, and each burst hands you an awaitable that carries its outcome (completed, faulted or canceled). A hand-rolled `Task.Delay` + `CancellationTokenSource` loop covers the basic deferral, but coalescing, pause, limits and worker choice are then yours to write and test.
Plus, Aspetta lets you impose bounds on the throttling, so you can't find yourself postponing forever.

---

## Quick Example

```csharp
using Some.Restraint;
//first and only execution will happen after about 1.5s
var delay = Defer
    .ToExecute(() => Console.WriteLine("Action executed!"))
    .ForAtLeast(500)
    .Build();

// Simulate a burst of triggers
delay.Trigger(); //schedules the first execution at +500
for (int i = 0; i < 10; i++)
{
    Thread.Sleep(100);
    delay.Trigger();//resets at +500 every time,
                    //before the first execution has a chance to run
}
await delay.Pending;
```

This will only print **once**, about 500ms after the last trigger.

A delay of zero (or negative) forces the execution to start as soon as the worker can begin it. Intervals truncate to whole milliseconds. `Delay` is also a property you may change at runtime, but only affects future cycles; an armed cycle keeps the deadline it was armed with until a later trigger re-arms it with the new value.

Triggering is synchronous and never blocks on the worker: all members are safe to call from any thread, even concurrently; a trigger takes a short internal lock and returns. Its return value (typed `DeferredExecution`) can be used to await the next execution, probe into its outcome, or be simply ignored:

```csharp
delay.Trigger();                       // fire and forget
await delay.Trigger();                 // trigger, then await the delegated run
var execution = delay.Trigger(TimeSpan.FromMilliseconds(500)); // an awaitable value you can pass around or keep
```

Triggers are never dropped: one that arrives while the delegate is still executing arms a new, independent cycle, which runs after the current pass ends. If that new deadline falls due while the delegate is still running, the new pass starts as soon as the worker is free, without re-arming a further delay.

`Trigger(TimeSpan)`'s interval replaces the configured delay for that trigger only: every trigger re-anchors the deadline at its own moment plus its own interval, so when coalesced triggers pass different intervals, the last trigger's anchor wins. A `WithMaximumDelay` span still caps the total.

## Execution values

`Trigger`, `ExecuteNow` and `Pending` return a `DeferredExecution`: the execution the call was assigned to.

- **Completed**: the delegate ran and returned normally; awaiting returns normally
- **Faulted**: the delegate threw; awaiting rethrows its original exception
- **Canceled**: the run never started (canceled while pending, skipped while paused, or still pending at dispose), or the delegate aborted it by throwing `OperationCanceledException` while the run was canceled — on the run's token, or without naming any token; awaiting throws `TaskCanceledException`

Triggers that coalesce share one execution and outcome: a burst of triggers that resolves into a single run allocates one value, regardless of how long the burst lasts. The value carries its outcome, so it can be awaited at any time, long after the run resolved. For `Task` interop it exposes `AsTask()`, `IsCompleted` and an implicit `Task` conversion.

> **Warning:** a `default(DeferredExecution)` awaits forever. The struct's zero value has no execution behind it: `IsCompleted` reports false, and neither awaiting it nor any unbounded wait on it ever returns. Only values handed out by `Trigger`, `ExecuteNow` and `Pending` resolve. If your code needs an empty placeholder, default to nullable and check for null — never to the value's own default.

```csharp
// wait for the execution this trigger was assigned to (or coalesced into)
await delay.Trigger(TimeSpan.FromMilliseconds(500));

// observe what is already pending without contributing a trigger:
// awaits the pending execution, or completes immediately if nothing is pending
await delay.Pending;

// Task interop
await Task.WhenAll(delay.Trigger(TimeSpan.FromMilliseconds(500)), delay.ExecuteNow());
```

`ExecuteNow()` runs as soon as the worker can take it. If a cycle is armed when you call it, the armed execution and the immediate one are the same: the armed deadline is pulled to now and both calls' values resolve with that single run. `Pending` observes armed cycles only: while a run is executing, it completes immediately, because a running pass is not pending.

> **Warning:** never wait on a Defer instance from inside its own delegate. Triggering it from there is fine and a common pattern: `_ = delay.Trigger();` (fire-and-forget), `ExecuteNow()`, `CancelPending()`, `CancelExecuting()`, `Pause()`, `Resume()` and `Dispose()` may all be used from the delegate. But awaiting or blocking on any of its execution values from within the delegate is a sure path to a deadlock: the delegate occupies the only worker, and the execution it waits on can only be produced by that worker. This cannot be guarded against reliably because an execution value can be stored and awaited from any other context, where doing so is legitimate; so it is stated as a caller-side rule.
>
> **Warning:** callbacks registered on a run's `CancellationToken` execute synchronously on the thread that called `Cancel`, `CancelExecuting` or `Dispose`. Keep them short and non-blocking: a callback that blocks on the Defer's worker — or on anything the in-flight delegate is waiting for — can deadlock, and a `Dispose` issued from inside such a callback is detected like a self-dispose and returns without waiting for the worker. A callback that throws surfaces its exception on the caller of `Cancel`/`Dispose`.

## Asynchronous delegates

The task-based worker accepts asynchronous delegates; while they run, no thread is held at all.

```csharp
var delay = Defer
    .UsingTask()
    .ToExecute(async (CancellationToken ct) => await SaveAsync(ct))
    .ForAtLeast(1000)
    .Build();
```

`CancelExecuting()` cancels the in-flight execution through that token: a delegate that honors it by throwing `OperationCanceledException` on that token resolves its execution as canceled, not faulted; so does one that throws an `OperationCanceledException` without naming any token while the run is canceled. A delegate that instead observes the cancellation and returns normally resolves its execution as completed, while one that throws an `OperationCanceledException` blaming an unrelated token resolves as faulted: the outcome is what the delegate actually did. Asynchronous delegates must be registered as `Func<Task>` or `Func<CancellationToken, Task>`; registering an async delegate against a synchronous overload is rejected by the builder at runtime instead of silently becoming an uncancelable fire-and-forget.

## Limit Options

Each trigger pushes the execution back by the whole delay. You can bound that behavior:

- `WithMaximumDelay`: execution happens at most N ms after the first trigger,
- `WithMaximumTriggerLimit`: the Nth trigger executes immediately instead of pushing the deadline again,
- or both, whichever fires first.

The limits are configured on the builder and are read-only afterwards: the `MaximumDelaySpan` and `MaximumTriggerLimit` properties on a built Defer are getters only.

```csharp
var delay = Defer
    .ToExecute(SaveData)
    .ForAtLeast(1000)
    .WithMaximumDelay(20000)
    .WithMaximumTriggerLimit(5)
    .Build();
```

Both limits hold at their lower ends: a trigger limit of 1, or a span of 0, means every trigger executes immediately, which turns the debouncer into a plain pass-through.

## Threading Options

You can choose whether the delay logic runs on:

- A task-based worker (default): a fully asynchronous loop that holds no thread while idle or while an async execution is in flight. Accepts synchronous and asynchronous delegates. The loop is scheduled on `TaskScheduler.Default`; pass a `TaskFactory` to `UsingTask(...)` to pin it to a custom scheduler instead.
- A dedicated thread: runs the delegate synchronously on its own background thread, useful when interacting with contexts that require some kind of thread affinity. Any async delegate handed to the `UsingDedicatedThread` side of the builder is rejected at build time.

```csharp
var delay = Defer
    .UsingDedicatedThread()
    .ToExecute(ProcessBatch)
    .ForAtLeast(200)
    .Build();
```

## Canceling and pausing

```csharp
delay.CancelPending();  // consumes the armed execution or forgets a pass a pause swallowed; its value resolves as canceled
delay.CancelExecuting();// cancels the in-flight run through the delegate's token; assuming of course that the delegate respects the CancellationToken
delay.Cancel();         // both
delay.Pause();          // holds pending executions until resumed
delay.Resume();         // releases the hold: whatever it held keeps its schedule
delay.Resume(TimeSpan.FromSeconds(30)); // the same, re-anchored to now + 30s
```

Pause never disturbs a run already executing: it holds *pending* executions. A cycle that comes due while paused is consumed and its execution value resolves as canceled, but the pause promises a re-arm at the resume; `CancelPending`, explicitly or through `Cancel()`, forgets that promise. Pause holds an `ExecuteNow` the same way: its pass is consumed and resolves as canceled. `IsPaused` reads whether the hold is on.

Resume re-arms what the pause holds, if it holds anything — it never creates a run by itself:

| | nothing held | a pass was consumed while paused | a cycle is still armed |
|---|---|---|---|
| `Resume()` | nothing happens | a fresh cycle at the configured delay | fires at its original deadline (re-anchored on the time the cycle had left; a spent remaining time executes as soon as the worker can) |
| `Resume(interval)` | nothing happens | a fresh cycle at now + interval | re-anchored at now + interval |

The re-arm counts as one trigger toward the cycle's trigger limit, and cannot push the run past the burst's maximum delay span anchor.

To guarantee a run after resuming regardless of what the pause holds, trigger **just before** resuming, in that order: Trigger unconditionally arms or coalesces into whatever the pause holds, and Resume then releases it unchanged. The pattern is safe to run even without knowing whether the Defer is paused: `Resume` on an unpaused Defer is an idempotent no-op. Triggering *after* resuming is equivalent in the common cases, with one caveat: a held cycle whose deadline has already spent fires immediately at the resume (its remaining time is in the past), and the later trigger then schedules one further run. Triggering first re-anchors that stale deadline away, and exactly one run happens.

> **Pause versus limits.** The limits are configured by you before `Build()`, and the pause/resume machinery at runtime, so resume follows your intent. The one limit that still acts during a pause is the maximum-delay span: it is anchored to the burst, not to the pause, and a span that expires while paused consumes the due cycle as canceled at the span, like any other due pass. The re-arm of a resume behaves as **one more trigger**, and the limits apply to it exactly as to any other:
>
> - a pass that was consumed while paused has already resolved its value as canceled; its promised re-arm is a fresh cycle, anchored anew from the resume: it waits the configured delay (or the given interval), and its trigger count and maximum delay span anchor anew from the resume;
> - a cycle still armed when you paused (you resumed before its deadline) keeps that deadline: the resume re-anchors on the time the cycle had left, so a pause holds an armed cycle without restarting its window, and a per-trigger override interval that armed it survives the pause. The resume counts as one trigger toward its trigger limit (with a limit of 2, a pause/resume alone can fire it immediately), and the burst's maximum delay span anchor keeps the resume from slipping past it.

## Errors

A faulting delegate faults the execution value with the original exception, and (if configured) is reported to the handler. Configure one to observe faults from fire-and-forget triggers that nobody awaits:

```csharp
var delay = Defer
    .ToExecute(SaveData)
    .ForAtLeast(1000)
    .WithErrorHandler(ex => _log.Warn("save failed", ex))
    .Build();
```

The handler is invoked defensively: if it throws, the worker survives and the execution still faults with the delegate's original exception.

## Blocking forms

Blocking and `Task`-returning forms exist as extensions over the execution values, for synchronous call sites:

```csharp
delay.TriggerAndWait(TimeSpan.FromMilliseconds(500));               // blocks until the execution resolves
delay.TriggerAndWait();                  // no interval: the configured delay
await delay.TriggerAndWaitAsync(TimeSpan.FromMilliseconds(500));    // the same, as a Task
delay.ExecuteNowAndWait();               // immediate execution, blocked until done
await delay.WaitForNextExecutionAsync(); // waits for whatever is pending, if anything
```

The blocking forms surface the execution's outcome directly (without an `AggregateException` wrapper): the delegate's original exception on fault, `TaskCanceledException` on cancellation.

## Debugging

Defers describe themselves to the debugger. Hovering one at a trigger site reads like `save: SaveViewModel.SaveAsync after 300 ms - armed (3 triggers)`: the leading name comes from the builder's `Named(...)` call, also readable at runtime as the `Name` property. Expanding it shows the registered delegate with its method and target, the worker kind, the armed trigger count, and the pending cycle's status. Executing value variables hover as their run status. The scheduling internals are transparent to stepping: a step-into across a trigger lands in user code, and the way to stop inside the delegate is a breakpoint in it.

For "why did it fire *now*?" questions, opt into trigger provenance. The debugger view then also shows who opened the current execution cycle, who triggered last, and the realized debounce of the last run:

```csharp
var delay = Defer
    .ToExecute(SaveData)
    .ForAtLeast(1000)
    .WithTriggerTracing()
    .Named("save")
    .Build();
```

Trigger tracing is compiled into DEBUG and RELEASE builds alike, so it works in the Release-built NuGet package too: the provenance is recorded by the instances that were built with it, in any configuration. The stack captures are paid only by traced instances (a `WithTriggerTracing()` Defer records at every trigger; an untraced one pays a single flag check). Because the option is on the builder, a production process you just attached the debugger to can still start telling you things: build a traced `Defer` from the immediate window — even against the same delegate — and watch its triggers while the faulty behavior reproduces, without recompiling or restarting the runtime. For a concurrency library, where failures are rarely deterministic, that is the difference between a repro and a rumor.

## Lifetime

- `Dispose` cancels the in-flight run through its token (a delegate that honors the token ends promptly), waits for the delegate to return, and resolves every outstanding execution value that never began a run as canceled; it can be called repeatedly. The in-flight run itself resolves with its own outcome: canceled if it throws an `OperationCanceledException` attributable to that token (thrown on it, or without naming any token), faulted on any other exception, completed if it returns normally — so a delegate that disposes its own Defer as part of its work and then completes is a completed run, not a canceled one.
- `Dispose` issued from inside the delegate is detected: the worker shuts itself down instead of waiting on itself. Detection follows the `ExecutionContext`, so it also covers the delegate's await continuations and tasks spawned from it — a `Dispose` from any of those returns without waiting. A `Dispose` issued from inside a cancellation callback of the run's token is detected the same way (callbacks run synchronously on the canceling thread, and the worker may be blocked on them). A `Dispose` from any other context always waits for the worker to finish, even if a racing self-dispose already ran.
- A Defer that becomes unreachable without being disposed is not leaked: its worker notices the dead reference within about a second and shuts itself down; a run already in flight always finishes, even if its wrapper is collected while it runs.
- After dispose, the mutating surface (`Trigger`, `ExecuteNow`, `Cancel` with its narrower forms, the `Delay` setter, `Pause`, `Resume`, `Pending`, and the blocking forms) throws `ObjectDisposedException`; reading the instance's configuration (`Name`, `Delay`, `LimitType`, `IsPaused`) remains harmless.

The edge case of a callback registered on the run's `CancellationToken` throwing during a `Cancel` or `Dispose` is documented in the XML documentation on `Cancel` and `Dispose`.

## What's up with the naming chaos?

- The library name **Aspetta** means "Wait!" in Italian.
- **Defer** as the main class' name was chosen to flow with the fluent builder, which is just about the only time you will ever write that name in the most common use cases:

    **Defer** -> **ToExecute** -> *what* -> **ForAtLeast** -> *how much*

    After building an instance, you typically just call `Trigger` on the built reference.
- **Some.Restraint** as main namespace is just a joke, since it appears in the usings section as:

    ```csharp
    using Some.Restraint;
    ```
    Though silly, it's still an appropriate summary of what the library offers: a way to stop invoking hot paths too often.

## Internals and the test friend

`Aspetta.dll` declares its internals visible to any assembly named `Aspetta.Tests` — that is the library's own test suite, which drives them directly rather than by reflection. Only what this README documents is the support surface: everything the friend assembly can see is internal, changes freely between releases without notice, and is reachable by any full-trust caller through ordinary reflection anyway. The declaration buys compile-checked tests, not a security boundary.

## License and credits

**Aspetta** is released under Apache 2.0

The project icon (also used for the NuGet package) was designed by [Freepik](https://www.flaticon.com/authors/freepik)

Contributions are welcome.
