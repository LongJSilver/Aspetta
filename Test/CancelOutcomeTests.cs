using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     The worker-parameterized \"CancelOutcomeTests\" half of the suite: every test runs once against the
///     task-based worker and once against the dedicated thread, through DeferrerTestFactory.
/// </summary>
public abstract class CancelOutcomeTests
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
    public async Task CancelPendingTest()
    {
        // Arrange
        var executionCount = 0;
        var LongAction = new DeferAction(() => { Interlocked.Increment(ref executionCount); });

        using Defer delayer = GetDeferrer(1000, LongAction);
        // Act
        DeferredExecution execution = delayer.Trigger();
        await Task.Delay(100); // inside the debounce window
        delayer.CancelPending();
        // Assert: the cycle is consumed and its execution value resolves as canceled
        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public async Task CancelExecutingTest()
    {
        // Arrange: park the delegate on the cancellation token itself, so that the
        // cancel is the only possible wake-up and the run must resolve as canceled
        var inFlight = new ManualResetEventSlim();
        var LongAction = new DeferCancellableAction(token =>
        {
            inFlight.Set();
            token.WaitHandle.WaitOne(); // released only by the cancellation
            throw new OperationCanceledException(token);
        });

        using Defer delayer = GetDeferrer(50, LongAction);
        // Act
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(2000)); // really executing now
        delayer.CancelExecuting();
        // Assert: the run resolves as canceled
        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
    }

    [Fact]
    public async Task CancelPending_DuringExecution_Should_Not_Affect_CurrentExecution()
    {
        // Arrange: park the delegate in flight
        var executionCount = 0;
        var inFlight = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var LongAction = new DeferAction(() =>
        {
            inFlight.Set();
            release.Wait(5000);
            Interlocked.Increment(ref executionCount);
        });
        using Defer delayer = GetDeferrer(50, LongAction);
        // Act
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(2000));
        delayer.CancelPending(); // nothing is pending: must not disturb the running pass
        release.Set();
        await execution;
        // Assert
        Assert.Equal(1, executionCount); // The action should have executed once
    }

    [Fact]
    public async Task CancelExecuting_WhilePending_Should_Not_Cancel_Pending()
    {
        // Arrange
        var executionCount = 0;
        var LongAction = new DeferAction(() => { Interlocked.Increment(ref executionCount); });
        using Defer delayer = GetDeferrer(200, LongAction);
        // Act
        DeferredExecution execution = delayer.Trigger();
        delayer.CancelExecuting(); // nothing to cancel: must not consume the pending cycle
        await execution;
        // Assert
        Assert.Equal(1, executionCount); // The action should have executed once
    }

    [Fact]
    public async Task An_OCE_Blaming_An_Unrelated_Token_Should_Fault_The_Run()
    {
        // The outcome is what the delegate actually did: an OCE that blames some
        // other token is a fault like any other, even when the run's own token was
        // canceled in the same breath. Only an OCE attributable to the canceled
        // run — thrown on its token, or without naming any token — counts as
        // canceled.
        var other = new CancellationTokenSource();
        var inFlight = new ManualResetEventSlim();
        Exception? seenByHandler = null;
        using Defer delayer = GetDeferrer(10, (CancellationToken ct) =>
        {
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000); // released by CancelExecuting
            throw new OperationCanceledException(other.Token); // blames an unrelated token
        }, onErrorHandler: ex => seenByHandler = ex);

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(inFlight.Wait(5000));
        delayer.CancelExecuting(); // the run's own token is canceled too, by then

        Task task = execution.AsTask();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsType<OperationCanceledException>(ex); // faulted, not TaskCanceledException
        Assert.Equal(other.Token, ex.CancellationToken); // the delegate's blame, preserved
        Assert.Equal(TaskStatus.Faulted, task.Status);
        Assert.Same(ex, seenByHandler); // a fault is always reported to the handler
    }

    [Fact]
    public async Task A_Tokenless_OCE_Thrown_While_Canceled_Should_Resolve_As_Canceled()
    {
        // A delegate that observes the run's cancellation with a parameterless OCE
        // (no token to name) still resolves its execution as canceled; the handler
        // stays out of it, as for every canceled run.
        var inFlight = new ManualResetEventSlim();
        var handlerRuns = 0;
        using Defer delayer = GetDeferrer(10, (CancellationToken ct) =>
        {
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000); // released by CancelExecuting
            throw new OperationCanceledException(); // no token named at all
        }, onErrorHandler: _ => handlerRuns++);

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(inFlight.Wait(5000));
        delayer.CancelExecuting();

        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
        Assert.Equal(0, handlerRuns); // canceled is not a fault: nothing to report
    }

    [Fact]
    public async Task A_Delegate_That_Observes_The_Cancellation_And_Returns_Normally_Should_Complete()
    {
        // The outcome is what the delegate actually did: observing the
        // cancellation and returning normally is a completed run, not canceled.
        var inFlight = new ManualResetEventSlim();
        var ran = 0;
        using Defer delayer = GetDeferrer(10, (CancellationToken ct) =>
        {
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000); // observes the cancellation,
            ran++;
        });                               // then returns normally

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(inFlight.Wait(5000));
        delayer.CancelExecuting();

        await execution; // resolves normally: completed, never canceled
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task The_Error_Handler_Should_Stay_Silent_On_A_Canceled_Run()
    {
        // A canceled run is not a fault: the handler observes faults only, and
        // the OCE thrown on the run's own token is never reported to it.
        var inFlight = new ManualResetEventSlim();
        var handlerRuns = 0;
        using Defer delayer = GetDeferrer(10, (CancellationToken ct) =>
        {
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000); // released by CancelExecuting
            throw new OperationCanceledException(ct);
        }, onErrorHandler: _ => handlerRuns++);

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(inFlight.Wait(5000));
        delayer.CancelExecuting();

        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
        Assert.Equal(0, handlerRuns);
    }

    [Fact]
    public async Task Error_Handler_Should_Receive_The_Delegates_Exception()
    {
        Exception? seen = null;
        using Defer delayer = GetDeferrer(50,
            _ => throw new InvalidOperationException("boom"),
            onErrorHandler: ex => seen = ex);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => delayer.Trigger(TimeSpan.FromMilliseconds(50)).AsTask());

        Assert.Same(ex, seen); // the handler observed the very exception the value rethrows
    }

    [Fact]
    public async Task Throwing_Error_Handler_Should_Not_Kill_The_Worker()
    {
        var runs = 0;
        using Defer delayer = GetDeferrer(50,
            () =>
            {
                runs++;
                throw new InvalidOperationException("boom");
            },
            onErrorHandler: _ => throw new Exception("handler boom"));

        // the execution still faults with the delegate's original exception
        DeferredExecution first = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => first.AsTask());
        Assert.Equal("boom", ex.Message);

        // and the worker survived: later cycles still execute and resolve
        DeferredExecution second = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.AsTask());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Trigger_Token_Should_Be_Canceled_When_Pending_Is_Canceled()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(2000, () => executionCount++);

        DeferredExecution wait = delayer.Trigger(TimeSpan.FromMilliseconds(2000));
        delayer.CancelPending();

        await Assert.ThrowsAsync<TaskCanceledException>(() => wait.AsTask());
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public async Task Canceling_A_Later_Pending_Cycle_Should_Not_Release_The_Running_Token()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () =>
        {
            executionCount++;
            Thread.Sleep(300); // slow delegate: run 1 spans ~50ms to ~350ms
        });

        DeferredExecution running = delayer.Trigger(TimeSpan.FromMilliseconds(50)); // run 1 starts at ~50ms
        await Task.Delay(100); // now mid-flight
        DeferredExecution later = delayer.Trigger(TimeSpan.FromMilliseconds(2000)); // arm a much later cycle
        delayer.CancelPending(); // and cancel it while run 1 is in flight

        await Assert.ThrowsAsync<TaskCanceledException>(() => later.AsTask()); // only the later token
        Assert.False(running.IsCompleted); // the running token must not be released early
        await running;
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task Cancel_Racing_Dispose_Should_Only_Throw_The_Documented_ObjectDisposedException()
    {
        for (var i = 0; i < 500; i++)
        {
            using Defer delayer = GetDeferrer(50, () => { });
            _ = delayer.Trigger(TimeSpan.FromMilliseconds(5000));
            Task dispose = Task.Run(() => delayer.Dispose());
            try
            {
                delayer.CancelPending();
            }
            catch (ObjectDisposedException ex)
            {
                // Only the documented ODE (from ThrowIfDisposed, named "Defer") may
                // escape; one from a disposed primitive (e.g. the signal) is a race.
                Assert.Equal(nameof(Defer), ex.ObjectName);
            }
            await dispose;
        }
    }

    [Fact]
    public async Task Cancel_All_Should_Cancel_The_Pending_Even_If_A_Token_Callback_Throws()
    {
        var inFlight = new ManualResetEventSlim();
        using Defer delayer = GetDeferrer(50, (CancellationToken ct) =>
        {
            ct.Register(() => throw new Exception("callback boom"));
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000);
            throw new OperationCanceledException(ct);
        });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(5000));
        DeferredExecution later = delayer.Trigger(TimeSpan.FromMilliseconds(5000)); // armed next cycle

        // the throwing callback surfaces on the Cancel caller (the BCL aggregates
        // token-callback exceptions): pin the original by identity, so silently
        // swallowing would fail too
        var surfaced = Assert.Throws<AggregateException>(() => delayer.Cancel());
        var original = Assert.Single(surfaced.InnerExceptions);
        Assert.Equal("callback boom", original.Message);

        // the pending cycle must already be canceled, despite the throwing callback
        await Assert.ThrowsAsync<TaskCanceledException>(() => later.AsTask());
    }

    [Fact]
    public void A_Source_Granted_To_A_Racing_Cancel_Must_Survive_The_Pass_End()
    {
        // A Cancel or Dispose reads the run's source under the lock while the run
        // is in flight and calls Cancel() on it outside the lock; the run may end
        // in between. The worker retires the source rather than disposing it at the
        // pass end, so the racing cancel still reaches the registered callbacks:
        // disposing mid-notify would violate the CTS contract and can skip one.
        // This pins the retire-not-dispose mechanism (a reservation regression
        // surfaces here as an ObjectDisposedException). The disposal grace itself
        // is best effort — a pass due immediately can compress it to milliseconds —
        // and cannot be pinned deterministically; on the current BCL a mid-notify
        // disposal is benign anyway.
        var inFlight = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var fired = new ManualResetEventSlim();
        using Defer delayer = GetDeferrer(10, (CancellationToken ct) =>
        {
            ct.Register(() => fired.Set());
            inFlight.Set();
            release.Wait(5000); // the delegate ends exactly when released
        });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(inFlight.Wait(5000));

        var core = delayer.Core; // internal: visible to this assembly

        // exactly what a racing CancelExecuting does: take the source mid-run
        CancellationTokenSource source = core._CurrentCancellation!;

        release.Set(); // the run ends; the worker retires the source at the pass end
        var guard = Stopwatch.StartNew();
        while (core._CurrentCancellation != null)
        {
            if (guard.ElapsedMilliseconds > 3000)
                Assert.Fail("the run's source was never retired at the pass end");
            Thread.Sleep(10);
        }
        Assert.False(fired.IsSet); // nobody has notified the registration yet

        // a cancel racing the pass end still reaches the retired source's
        // registrations, instead of racing the disposal onto a dead source
        source.Cancel();
        Assert.True(fired.Wait(5000),
            "the cancel racing the pass end never reached the registered callbacks");
    }

}

public sealed class CancelOutcomeTestsUsingTask : CancelOutcomeTests
{
    protected override bool UsesDedicatedThread => false;
}

public sealed class CancelOutcomeTestsUsingThread : CancelOutcomeTests
{
    protected override bool UsesDedicatedThread => true;
}
