using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     The worker-parameterized \"DebounceTests\" half of the suite: every test runs once against the
///     task-based worker and once against the dedicated thread, through DeferrerTestFactory.
/// </summary>
public abstract class DebounceTests
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
    public async Task Delayer_Should_Execute_Action_After_Delay()
    {
        // Arrange
        var actionExecuted = false;
        using Defer delayer = GetDeferrer(100, () => actionExecuted = true);

        // Act
        await delayer.Trigger();

        // Assert
        Assert.True(actionExecuted);
    }

    [Fact]
    public void Delayer_Should_Not_Execute_Action_If_Canceled()
    {
        // Arrange
        var actionExecuted = false;
        using Defer delayer = GetDeferrer(100, () => actionExecuted = true);

        // Act
        delayer.Trigger();
        delayer.CancelPending();
        Thread.Sleep(150); // Wait to ensure action is not executed

        // Assert
        Assert.False(actionExecuted);
    }

    [Fact]
    public async Task Delayer_Should_Debounce_Multiple_Triggers()
    {
        // Arrange
        var executionCount = 0;
        using Defer delayer = GetDeferrer(100, () => executionCount++);

        // Act: every trigger coalesces into one cycle; the last one's value
        // is that shared cycle, so awaiting it waits for the single execution
        _ = delayer.Trigger();
        Thread.Sleep(20);
        _ = delayer.Trigger();
        Thread.Sleep(20);
        await delayer.Trigger();

        // Assert
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task Delayer_Should_Execute_Action_Immediately_With_ExecNow()
    {
        // Arrange
        var actionExecuted = false;
        const int Delay = 100;
        var st = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(Delay, () =>
        {
            st.Stop();
            actionExecuted = true;
        });

        // Act
        await delayer.ExecuteNow();

        // Assert
        Assert.True(actionExecuted);
        Assert.True(st.ElapsedMilliseconds < Delay);
    }

    [Fact]
    public async Task ExecuteNow_Should_Pull_The_Armed_Cycle_And_Share_Its_Execution()
    {
        // A cycle is armed when ExecuteNow is called: the armed deadline is pulled
        // to now and both calls resolve with the very same execution.
        var ran = 0;
        using Defer delayer = GetDeferrer(5000, () => ran++);

        DeferredExecution armed = delayer.Trigger(TimeSpan.FromMilliseconds(5000));
        DeferredExecution now = delayer.ExecuteNow();

        Assert.Same(armed.AsTask(), now.AsTask()); // one shared execution
        await now;
        Assert.True(armed.IsCompleted);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Implicit_Task_Conversion_And_WhenAll_Should_Compose()
    {
        // The README's composition: an armed trigger and an ExecuteNow resolve
        // together as one run, through the implicit Task conversion.
        var ran = 0;
        using Defer delayer = GetDeferrer(500, () => ran++);

        Task first = delayer.Trigger(TimeSpan.FromMilliseconds(500)); // implicit conversion
        Task now = delayer.ExecuteNow(); // pulls the armed cycle: one run for both

        await Task.WhenAll(first, now);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Trigger_Int_And_UInt_Extensions_Should_Pipe_Into_The_TimeSpan_Form()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () => executionCount++);

        await delayer.Trigger(50);   // int extension -> Trigger(TimeSpan)
        await delayer.Trigger(50u);  // uint extension -> Trigger(TimeSpan)

        Assert.Equal(2, executionCount);
    }

    [Fact]
    public async Task Trigger_Token_Should_Wait_Until_Execution_Completes()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () => executionCount++);

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, executionCount);

        DeferredExecution wait = delayer.Trigger(TimeSpan.FromMilliseconds(500));
        await Task.Delay(200);
        Assert.False(wait.IsCompleted); // must not complete before the execution
        await wait;
        Assert.Equal(2, executionCount);
    }

    [Fact]
    public async Task Trigger_Token_Should_Fault_With_The_Delegate_Exception()
    {
        using Defer delayer = GetDeferrer(50, _ => throw new InvalidOperationException("boom"),
            onErrorHandler: _ => { });

        DeferredExecution wait = delayer.Trigger(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait.AsTask());
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Coalesced_Triggers_Should_Share_The_Cycle_Outcome()
    {
        using Defer delayer = GetDeferrer(100, _ => throw new InvalidOperationException("boom"),
            onErrorHandler: _ => { });

        DeferredExecution first = delayer.Trigger(TimeSpan.FromMilliseconds(100));
        DeferredExecution second = delayer.Trigger(TimeSpan.FromMilliseconds(100));

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => first.AsTask());
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => second.AsTask());
        Assert.Same(ex1, ex2); // one execution, one shared outcome
    }

    [Fact]
    public async Task Trigger_During_Execution_Binds_To_The_Next_Cycle()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () =>
        {
            executionCount++;
            Thread.Sleep(150); // slow delegate: the first run is still in flight
        });

        DeferredExecution first = delayer.Trigger(TimeSpan.FromMilliseconds(50)); // run 1 starts at ~50ms
        await Task.Delay(100); // now mid-flight
        DeferredExecution second = delayer.Trigger(TimeSpan.FromMilliseconds(400)); // binds to run 2, armed well past run 1's end

        await first;
        Assert.Equal(1, executionCount); // run 1 done; second must not have been released by it
        Assert.False(second.IsCompleted);

        await second;
        Assert.Equal(2, executionCount);
    }

    [Fact]
    public async Task A_Deadline_Falling_Due_While_The_Delegate_Runs_Should_Start_The_Next_Pass_When_The_Worker_Is_Free()
    {
        // A trigger that arrives while the delegate is executing arms a new
        // independent cycle; when that cycle's deadline falls due before the
        // running pass ends, the new pass starts as soon as the worker is free,
        // without re-arming a further delay.
        var runs = 0;
        var secondRunStartedAtMs = -1L;
        var firstRunIn = new ManualResetEventSlim();
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(5000, () =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                firstRunIn.Set();
                Thread.Sleep(300); // the first pass spans its successor's deadline
            }
            else
            {
                secondRunStartedAtMs = stopwatch.ElapsedMilliseconds;
            }
        });

        // run 1: starts almost immediately, in flight until ~300ms
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(firstRunIn.Wait(5000));

        // arm run 2 while run 1 executes, with a deadline at ~200ms: due before
        // run 1 ends (~310ms)
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(200));

        await delayer.Pending;
        // started as soon as the worker was free (~310ms), not re-armed (that
        // would land at ~310+200ms at the earliest), and never early
        Assert.InRange(secondRunStartedAtMs, 150, 450);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Coalesced_Triggers_With_Different_Intervals_Last_Anchor_Wins()
    {
        // Every trigger re-anchors the deadline at its own moment plus its own
        // interval: coalesced triggers passing different intervals let the last
        // trigger's anchor govern.
        var ran = 0;
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(5000, () =>
        {
            ran++;
            ranAtMs = stopwatch.ElapsedMilliseconds;
        });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(600)); // arms at ~600
        Thread.Sleep(50);
        await delayer.Trigger(TimeSpan.FromMilliseconds(200)); // re-anchors at ~250

        Assert.InRange(ranAtMs, 150, 450); // the LAST trigger's anchor governs, not the first's 600
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Trigger_From_Inside_The_Delegate_Should_Arm_The_Next_Cycle()
    {
        // The documented fire-and-forget arming pattern, from within the delegate.
        var runs = 0;
        Defer? delayer = null;
        delayer = GetDeferrer(50, () =>
        {
            runs++;
            if (runs == 1) _ = delayer!.Trigger(TimeSpan.FromMilliseconds(100));
        });

        await delayer!.Trigger(TimeSpan.FromMilliseconds(50)); // first run re-arms the next cycle
        await Task.Delay(600);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Runtime_Delay_Change_Should_Not_Re_Deadline_An_Armed_Cycle()
    {
        // Delay is applied to the cycles armed after the change; the cycle armed
        // before it keeps the deadline it was armed with.
        var ranAt = -1L;
        var sw = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(400, () => ranAt = sw.ElapsedMilliseconds);

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(400)); // armed at ~400ms
        delayer.Delay = TimeSpan.FromMilliseconds(5000);      // only new triggers use this

        await delayer.Pending;                               // observes the cycle armed at 400ms
        Assert.True(ranAt >= 300 && ranAt <= 1500, $"ran at {ranAt}ms");
    }

    [Fact]
    public async Task A_Trigger_Emitted_After_A_Delay_Change_Should_Use_The_New_Delay()
    {
        // The configured delay, not the built-in one, governs every cycle armed
        // after the change: the value is read at arm time.
        var ran = 0;
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(3000, () =>
        {
            ran++;
            ranAtMs = stopwatch.ElapsedMilliseconds;
        });

        delayer.Delay = TimeSpan.FromMilliseconds(50);
        await delayer.Trigger(); // parameterless: the new configured delay

        Assert.Equal(1, ran);
        Assert.True(ranAtMs < 1500, $"ran at {ranAtMs}ms: the built-in 3000ms delay governed");
    }

    [Fact]
    public async Task An_Armed_Cycle_ReTriggered_After_A_Delay_Change_ReArms_With_The_New_Value()
    {
        // An armed cycle keeps its deadline "until a later trigger re-arms it
        // with the new value": the later trigger pulls a fresh deadline from the
        // CHANGED delay, discarding the original anchor and the override the
        // first trigger carried (which never leaks into the configuration).
        var ran = 0;
        using Defer delayer = GetDeferrer(200, () => ran++);

        DeferredExecution first = delayer.Trigger(TimeSpan.FromMilliseconds(250)); // override, for this trigger only
        delayer.Delay = TimeSpan.FromMilliseconds(1500);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), delayer.Delay); // the override did not leak

        Thread.Sleep(30); // a determinately later arm moment
        DeferredExecution reArmed = delayer.Trigger(); // re-arms with the new configured value

        await Task.Delay(400);
        Assert.Equal(0, ran); // the original deadline was re-armed away, not kept

        await reArmed;
        Assert.Equal(1, ran);
        Assert.True(first.IsCompleted); // the first trigger coalesced into the re-armed cycle
    }

    [Fact]
    public async Task Pending_Should_Complete_Immediately_When_Nothing_Is_Pending()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () => executionCount++);

        await delayer.Trigger(TimeSpan.FromMilliseconds(50));
        DeferredExecution pending = delayer.Pending;
        Assert.True(pending.IsCompleted); // nothing pending: no waiting
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task Pending_Should_Wait_For_The_Pending_Cycle_Without_Triggering()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(100, () => executionCount++);

        DeferredExecution wait = delayer.Trigger(TimeSpan.FromMilliseconds(300)); // arm the pending cycle
        DeferredExecution observer = delayer.Pending; // observe it without contributing a trigger
        Assert.False(observer.IsCompleted);

        await observer; // resolves with the same execution
        await wait;
        Assert.Equal(1, executionCount); // exactly one execution for both
    }

    [Fact]
    public async Task Pending_Should_Complete_Immediately_While_A_Run_Is_Executing()
    {
        // Pending observes armed cycles only: an in-flight run is not "pending".
        var inFlight = new ManualResetEventSlim();
        var ran = 0;
        using Defer delayer = GetDeferrer(50, () => { inFlight.Set(); Thread.Sleep(200); ran++; });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(5000)); // mid-run

        DeferredExecution pending = delayer.Pending;
        Assert.True(pending.IsCompleted); // nothing armed: completes immediately
        await pending;

        Thread.Sleep(250); // let the parked run finish before dispose
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Zero_Delay_Should_Execute_Immediately()
    {
        // No artificial floor: a configured zero closes the debounce window —
        // the pass starts as soon as the worker can begin it.
        var actionExecuted = false;
        var st = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(0, () => { st.Stop(); actionExecuted = true; });

        Assert.Equal(TimeSpan.Zero, delayer.Delay); // zero is preserved, not clamped

        await delayer.Trigger();

        Assert.True(actionExecuted);
        Assert.True(st.ElapsedMilliseconds < 1000); // no arming wait got in the way
    }

    [Fact]
    public async Task Negative_Trigger_Interval_Should_Execute_Immediately()
    {
        // A negative interval is just a deadline in the past.
        var actionExecuted = false;
        using Defer delayer = GetDeferrer(5000, () => actionExecuted = true);

        await delayer.Trigger(TimeSpan.FromSeconds(-1));

        Assert.True(actionExecuted);
    }

    [Fact]
    public async Task Integer_Trigger_Overloads_Should_Pipe_Into_The_TimeSpan_Form()
    {
        // Intervals in the past execute as soon as the worker can: the int form
        // can be negative, the uint form starts at zero.
        var ran = 0;
        using Defer delayer = GetDeferrer(5000, () => ran++);

        await delayer.Trigger(-1); // int extension: a deadline in the past
        Assert.Equal(1, ran);

        await delayer.Trigger(0u); // uint extension: due right now
        Assert.Equal(2, ran);
    }

}

public sealed class DebounceTestsUsingTask : DebounceTests
{
    protected override bool UsesDedicatedThread => false;
}

public sealed class DebounceTestsUsingThread : DebounceTests
{
    protected override bool UsesDedicatedThread => true;
}
