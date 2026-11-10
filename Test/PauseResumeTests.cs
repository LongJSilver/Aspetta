using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     The worker-parameterized \"PauseResumeTests\" half of the suite: every test runs once against the
///     task-based worker and once against the dedicated thread, through DeferrerTestFactory.
/// </summary>
public abstract class PauseResumeTests
{
    protected abstract bool UsesDedicatedThread { get; }

    protected Defer GetDeferrer(uint delayMS, DeferCancellableAction action, uint? maximumDelaySpan = null,
        uint? maximumTriggerLimit = null, Action<Exception>? onErrorHandler = null)
        => DeferrerTestFactory.Build(UsesDedicatedThread, delayMS, action, maximumDelaySpan, maximumTriggerLimit,
            onErrorHandler);

    protected Defer GetDeferrer(uint delayMS, DeferAction action, uint? maximumDelaySpan = null,
        uint? maximumTriggerLimit = null, Action<Exception>? onErrorHandler = null)
        => GetDeferrer(delayMS, ct => action(), maximumDelaySpan, maximumTriggerLimit, onErrorHandler);

    [Fact]
    public async Task Span_Expiring_While_Paused_Should_Consume_The_Cycle_As_Canceled()
    {
        // The span is anchored to the burst, not to the pause: at ~100ms the due pass
        // is consumed by the paused skip and its value resolves as canceled.
        var ran = 0;
        using Defer delayer = GetDeferrer(5000, () => ran++, maximumDelaySpan: 100);

        delayer.Pause();
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(5000));

        Task task = execution.AsTask();
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(2000)));
        await Assert.ThrowsAsync<TaskCanceledException>(() => task);

        // resuming re-arms a fresh cycle whose span anchors anew from the resume:
        // it fires ~100ms after the resume, long before the 5000ms delay
        delayer.Resume();
        await delayer.Pending;
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task CancelPending_While_Paused_Should_Forget_The_Consumed_Pass()
    {
        // A pass consumed while paused has already resolved its value as canceled,
        // but promised a re-arm on unpause: CancelPending must be able to recall
        // that promise too, not only a still-armed execution.
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () => executionCount++);

        delayer.Pause();
        DeferredExecution paused = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Task.Delay(250); // the due pass is consumed by the paused skip
        await Assert.ThrowsAsync<TaskCanceledException>(() => paused.AsTask()); // it really was consumed

        delayer.CancelPending(); // must forget the promised re-arm, not no-op
        delayer.Resume();
        await Task.Delay(250);

        Assert.Equal(0, executionCount);
    }

    [Fact]
    public async Task CancelPending_While_Paused_Should_Clear_The_Consumed_Pass_Even_If_Armed_Again()
    {
        // The tougher interleaving: the consumed pass left its re-arm promise, a new
        // trigger armed while paused, and CancelPending consumed that armed cycle.
        // The forgotten fossil must not survive the successful cancel.
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () => executionCount++);

        delayer.Pause();
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Task.Delay(250); // the due pass is consumed by the paused skip
        DeferredExecution armed = delayer.Trigger(TimeSpan.FromMilliseconds(5000)); // a new armed cycle

        delayer.CancelPending(); // consumes the armed cycle AND must forget the fossil
        await Assert.ThrowsAsync<TaskCanceledException>(() => armed.AsTask());
        delayer.Resume();
        await Task.Delay(250);

        Assert.Equal(0, executionCount);
    }

    [Fact]
    public async Task ExecuteNow_While_Paused_Should_Be_Held_And_Re_Armed_On_Resume()
    {
        // Pause holds an ExecuteNow like any other execution: its pass is consumed
        // by the pause, its value resolves as canceled, unpausing re-arms a fresh cycle.
        var ran = 0;
        using Defer delayer = GetDeferrer(50, () => ran++);

        delayer.Pause();
        DeferredExecution now = delayer.ExecuteNow();

        await Task.Delay(250); // the due pass is consumed by the paused skip
        Assert.Equal(0, ran);
        await Assert.ThrowsAsync<TaskCanceledException>(() => now.AsTask());

        delayer.Resume();
        await Task.Delay(300);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Resume_Should_Count_Toward_The_Trigger_Limit()
    {
        // "Pause versus limits": the re-arm on unpause counts as one trigger, so
        // with a limit of 2 a pause/resume alone reaches the limit and fires.
        var ran = 0;
        using Defer delayer = GetDeferrer(2000, () => ran++, maximumTriggerLimit: 2);

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(2000)); // armed, count 1
        delayer.Pause();
        await Task.Delay(100);
        var sw = Stopwatch.StartNew();
        delayer.Resume(); // the resume is trigger 2: fires immediately

        await Task.Delay(400);
        Assert.Equal(1, ran);
        // tight bound: a resume that wrongly re-anchored on anything but the
        // count limit (the ~1900ms remaining window, a fresh 2000ms) cannot
        // land here; only the immediate limit fire does
        Assert.True(sw.ElapsedMilliseconds < 600, $"executed after {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Resume_Of_A_Consumed_Pass_Should_Wait_A_Fresh_Full_Delay()
    {
        // A pass consumed by the pause re-arms as a fresh cycle: it does not
        // execute at the resume, it waits the full configured delay from there.
        var ranAt = -1L;
        var sw = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(150, () => ranAt = sw.ElapsedMilliseconds);

        delayer.Pause();
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(150));
        await Task.Delay(350); // consumed by the paused skip

        delayer.Resume(); // re-arms a fresh cycle, due ~150ms from now
        await Task.Delay(60);
        Assert.Equal(-1, ranAt); // still waiting the fresh window

        await Task.Delay(500);
        Assert.True(ranAt >= 0, "the fresh cycle did not execute");
    }

    [Fact]
    public async Task Resume_Of_An_Armed_Cycle_Should_Keep_Its_Deadline()
    {
        // A pause holds an armed cycle without restarting its window: the resume
        // re-anchors on the time the cycle had left, so the original deadline
        // (armed by a per-trigger override) survives the pause, override and all.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(5000, () => ranAtMs = stopwatch.ElapsedMilliseconds);

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(2000)); // override: deadline at ~2000
        delayer.Pause();
        await Task.Delay(1150);
        Assert.False(execution.IsCompleted); // the armed cycle is held, not consumed
        delayer.Resume(); // resume at ~1250: ~750ms of the original window left

        await Task.Delay(350); // ~1600ms: a window restart would still be pending
        Assert.False(execution.IsCompleted); // nothing fired early, either

        await execution; // fires at the original ~2000ms deadline
        Assert.InRange(ranAtMs, 1600, 2900); // not at resume+override (3250) nor resume+configured (6250)
    }

    [Fact]
    public async Task Resume_Of_A_ConfiguredDelay_Armed_Cycle_Should_Keep_Its_Deadline()
    {
        // The configured-delay arming keeps its deadline through a pause/resume
        // just as an override-armed one does.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(1000, () => ranAtMs = stopwatch.ElapsedMilliseconds);

        DeferredExecution execution = delayer.Trigger(); // the configured delay: deadline at ~1000
        delayer.Pause();
        await Task.Delay(550);
        delayer.Resume(); // resume at ~600

        await Task.Delay(250); // ~850ms: nothing may have fired yet
        Assert.False(execution.IsCompleted);

        await execution; // fires at the original ~1000ms deadline
        Assert.InRange(ranAtMs, 880, 1350); // not at resume+full-delay (1600)
    }

    [Fact]
    public async Task Resume_Of_An_Armed_Cycle_Should_Not_Slip_Past_The_Span()
    {
        // The burst's maximum delay span keeps its anchor across the pause: the
        // resume cannot slip the deadline past the span, even though the resume
        // re-arms by the time the cycle had left.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(3000, () => ranAtMs = stopwatch.ElapsedMilliseconds,
            maximumDelaySpan: 400);

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(3000)); // the span caps the deadline at ~400
        delayer.Pause();
        await Task.Delay(300);
        delayer.Resume(); // resume at ~350: close to the capped deadline

        await execution; // fires at the burst's span (~400ms), not re-anchored to resume+span (~750)
        Assert.InRange(ranAtMs, 340, 700);
    }

    [Fact]
    public async Task Resume_With_An_Interval_Should_Not_Schedule_When_Nothing_Is_Held()
    {
        // Resume never creates a run: with nothing held it only ends the pause,
        // whichever form is used. Folded en-passant: the bare Resume() schedules
        // nothing either, and the next run waits for a real trigger.
        var ran = 0;
        using Defer delayer = GetDeferrer(50, () => ran++);

        delayer.Pause();
        delayer.Resume(); // bare re-arm on an empty hold: nothing scheduled
        delayer.Resume(TimeSpan.FromMilliseconds(100)); // interval re-arm: nothing either

        await Task.Delay(300); // past the interval: no run was scheduled by either
        Assert.Equal(0, ran);

        await delayer.Trigger(); // the defer still works normally afterwards
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Resume_With_An_Interval_Should_ReAnchor_The_Held_Cycle()
    {
        // An interval re-anchors whatever the pause holds: the armed cycle's
        // surviving deadline (5s away) is replaced by resume + interval.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(50, () => ranAtMs = stopwatch.ElapsedMilliseconds);

        delayer.Pause();
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(5000)); // held, armed
        delayer.Resume(TimeSpan.FromMilliseconds(300)); // re-anchored at ~300ms

        await execution;
        Assert.InRange(ranAtMs, 150, 900); // not at the armed 5000ms, and not immediately
    }

    [Fact]
    public async Task Resume_With_An_Interval_Should_FreshAnchor_The_Consumed_Pass()
    {
        // A pass consumed while paused promised a fresh re-arm; an interval
        // anchors that fresh cycle at now + interval, instead of the
        // configured delay. The consumed pass itself stays canceled.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(5000, () => ranAtMs = stopwatch.ElapsedMilliseconds);

        delayer.Pause();
        DeferredExecution consumed = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Task.Delay(250); // due while paused: consumed, its value canceled
        await Assert.ThrowsAsync<TaskCanceledException>(() => consumed.AsTask());

        delayer.Resume(TimeSpan.FromMilliseconds(800)); // fresh cycle anchored at ~800ms
        await Task.Delay(400); // ~650ms: the fresh cycle must still be pending
        Assert.True(ranAtMs < 0, $"ran at {ranAtMs}ms");

        await delayer.Pending; // the fresh cycle resolves this
        Assert.True(ranAtMs >= 150 && ranAtMs <= 1400, $"ran at {ranAtMs}ms"); // ~800ms fired; the 5000ms configured never
    }

    [Fact]
    public async Task Triggering_Just_Before_Resuming_Should_Ensure_A_Run_When_Nothing_Is_Held()
    {
        // The guarantee pattern: Trigger while still paused arms a cycle the
        // resume then releases. The caller needed no knowledge of what the
        // pause held: with nothing held, the triggered cycle is the run.
        var ran = 0;
        using Defer delayer = GetDeferrer(5000, () => ran++);

        delayer.Pause();
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(100)); // while still paused
        delayer.Resume(); // releases the hold onto the triggered cycle

        await Task.Delay(600); // the ~100ms cycle fired; the configured 5000ms never
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Triggering_Just_Before_Resuming_Should_Ensure_A_Run_After_A_Consumed_Pass()
    {
        // The consumed pass's promised re-arm would wait the configured delay;
        // the pre-resume trigger replaces it with its own closer interval.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(5000, () => ranAtMs = stopwatch.ElapsedMilliseconds);

        delayer.Pause();
        DeferredExecution consumed = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Task.Delay(250); // due while paused: consumed, its value canceled
        await Assert.ThrowsAsync<TaskCanceledException>(() => consumed.AsTask());

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(200)); // ensure a run at ~200ms from now
        delayer.Resume();

        await delayer.Pending;
        Assert.True(ranAtMs >= 100 && ranAtMs <= 900, $"ran at {ranAtMs}ms"); // the trigger's 200ms, not the configured 5000ms
    }

    [Fact]
    public async Task Triggering_Just_Before_Resuming_Should_ReAnchor_The_Held_Cycle()
    {
        // The held armed cycle's own deadline (5s away) is re-anchored by the
        // pre-resume trigger's interval, exactly as every trigger re-anchors.
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(50, () => ranAtMs = stopwatch.ElapsedMilliseconds);

        delayer.Pause();
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(5000)); // held, armed
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(200)); // re-anchors the held cycle at ~200ms
        delayer.Resume();

        await execution;
        Assert.InRange(ranAtMs, 100, 900); // the trigger's 200ms, not the held 5000ms
    }

    [Fact]
    public async Task The_Resume_Pattern_Should_Be_Safe_When_Not_Paused()
    {
        // Resume on an unpaused defer is an idempotent no-op that does not
        // disturb armed cycles: the caller can run the
        // Trigger-then-Resume pattern without knowing whether the Defer is
        // paused at all.
        var ran = 0;
        using Defer delayer = GetDeferrer(50, () => ran++);

        delayer.Resume(); // not paused: no-op

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50)); // armed
        delayer.Resume(); // still no-op: the armed cycle runs on its own deadline

        await Task.Delay(400);
        Assert.Equal(1, ran); // exactly the one armed cycle, nothing else
    }

    [Fact]
    public async Task Trigger_Before_Resume_Should_Not_Fire_A_Past_Due_Held_Cycle()
    {
        // The transient: a held armed cycle whose deadline already spent while
        // the worker is parked on the core lock, so the pass was not consumed.
        // The pattern re-anchors the stale deadline: exactly one run happens,
        // at the trigger's interval — the overdue pass does not fire.
        var ran = 0;
        using Defer delayer = GetDeferrer(50, () => ran++);
        var core = delayer.Core; // internal: visible to this assembly

        delayer.Pause();
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(20)); // held; due in 20ms

        var taken = false;
        Monitor.Enter(core._lock, ref taken);
        try
        {
            Thread.Sleep(250); // the deadline spent unconsumed: the transient, frozen
            Assert.False(execution.IsCompleted); // neither fired nor canceled

            _ = delayer.Trigger(TimeSpan.FromMilliseconds(1500)); // the pattern, mid-transient
            delayer.Resume();
        }
        finally
        {
            Monitor.Exit(core._lock);
        }

        await Task.Delay(400);
        Assert.Equal(0, ran); // the overdue pass was NOT fired by the resume
        Assert.False(execution.IsCompleted); // still pending on the re-anchored deadline

        await execution; // resolves with the single re-anchored run
        Assert.Equal(1, ran);
    }

}

public sealed class PauseResumeTestsUsingTask : PauseResumeTests
{
    protected override bool UsesDedicatedThread => false;
}

public sealed class PauseResumeTestsUsingThread : PauseResumeTests
{
    protected override bool UsesDedicatedThread => true;
}
