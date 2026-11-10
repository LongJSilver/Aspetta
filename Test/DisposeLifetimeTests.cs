using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     The worker-parameterized \"DisposeLifetimeTests\" half of the suite: every test runs once against the
///     task-based worker and once against the dedicated thread, through DeferrerTestFactory.
/// </summary>
public abstract class DisposeLifetimeTests
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
    public async Task Dispose_From_Inside_The_Delegate_Should_Not_Deadlock()
    {
        Defer? delayer = null;
        var executed = new ManualResetEventSlim();
        delayer = GetDeferrer(50, () =>
        {
            delayer!.Dispose(); // runs on the worker: must not wait on itself
            executed.Set();
        });

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(executed.Wait(5000), "the delegate did not run within 5s");
        Task task = execution.AsTask();
        Assert.True(await Task.WhenAny(task, Task.Delay(5000)) == task,
            "the in-flight execution was not resolved after a self-dispose");
        // the run that issued the self-dispose completed its work: not canceled
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        delayer.Dispose(); // idempotent no-op
    }

    [Fact]
    public async Task Self_Dispose_Then_Throwing_OCE_Should_Resolve_The_Run_As_Canceled()
    {
        // The in-flight run keeps its own outcome: dispose cancels its token, and a
        // delegate that honors it by throwing OCE resolves the run as canceled.
        Defer? delayer = null;
        delayer = GetDeferrer(50, (CancellationToken ct) =>
        {
            delayer!.Dispose(); // self-dispose cancels this run's token
            ct.ThrowIfCancellationRequested();
        });

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
    }

    [Fact]
    public void External_Dispose_Racing_A_Self_Dispose_Should_Still_Wait_For_The_Delegate()
    {
        // Regression: when the self-dispose wins the lock first, a later external
        // Dispose must still wait for the in-flight delegate, not return early.
        var started = new ManualResetEventSlim();
        var selfDisposeReturned = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        long endedAt = -1, disposeReturnedAt = -1;
        var sw = Stopwatch.StartNew();
        Defer? delayer = null;
        delayer = GetDeferrer(10, () =>
        {
            started.Set();
            delayer!.Dispose(); // self-dispose: wins the lock first, returns at once
            selfDisposeReturned.Set();
            release.Wait(5000); // the delegate is still in flight afterwards
            endedAt = sw.ElapsedMilliseconds;
        });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(started.Wait(5000));
        Assert.True(selfDisposeReturned.Wait(5000)); // the self-dispose already ran

        var disposer = new Thread(() =>
        {
            delayer.Dispose();
            disposeReturnedAt = sw.ElapsedMilliseconds;
        });
        disposer.Start();
        Thread.Sleep(200); // the external Dispose must be parked in its wait by now
        release.Set();
        Assert.True(disposer.Join(5000));
        Assert.True(endedAt >= 0);
        Assert.True(disposeReturnedAt >= endedAt,
            $"external Dispose returned at {disposeReturnedAt}ms but the delegate ended at {endedAt}ms");
    }

    [Fact]
    public async Task Dispose_Should_Cancel_The_In_Flight_Execution_Token()
    {
        var inFlight = new ManualResetEventSlim();
        var observed = 0;
        using Defer delayer = GetDeferrer(50, (CancellationToken ct) =>
        {
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000); // released early only by cancellation
            if (ct.IsCancellationRequested) observed = 1;
            throw new OperationCanceledException(ct);
        });

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(5000));
        delayer.Dispose(); // must cancel the in-flight token, not wait out the 5s park

        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
        Assert.Equal(1, observed);
    }

    [Fact]
    public void Dispose_With_Throwing_Token_Callback_Should_Still_Wait_For_The_Delegate()
    {
        // Regression: a callback that throws during Dispose's token cancel must not
        // skip the wait for the in-flight delegate; the exception still surfaces on
        // the Dispose caller, but only after the worker has been joined.
        var inFlight = new ManualResetEventSlim();
        var finished = false;
        using Defer delayer = GetDeferrer(50, (CancellationToken ct) =>
        {
            ct.Register(() => throw new InvalidOperationException("callback boom"));
            inFlight.Set();
            ct.WaitHandle.WaitOne(5000); // released by Dispose's token cancel
            finished = true;
        });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(5000));
        // the callback surfaces on the Dispose caller (aggregated by the BCL,
        // as for Cancel), but only after the wait: pin both facts by identity
        var surfaced = Assert.Throws<AggregateException>(() => delayer.Dispose());
        var original = Assert.Single(surfaced.InnerExceptions);
        Assert.IsType<InvalidOperationException>(original);
        Assert.Equal("callback boom", original.Message);
        Assert.True(finished, "Dispose returned before the in-flight delegate finished");
    }

    [Fact]
    public void Dispose_From_A_Token_Callback_Should_Not_Deadlock()
    {
        // Regression: a token callback registered OUTSIDE the delegate runs on the
        // thread calling CancelExecuting; a Dispose from there must be detected like
        // a self-dispose instead of joining the worker, which is waiting on the callback.
        var inFlight = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        CancellationToken seen = default;
        using Defer delayer = GetDeferrer(50, (CancellationToken ct) =>
        {
            seen = ct;
            inFlight.Set();
            gate.Wait(8000); // released by the callback, only after its Dispose returns
        });

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(5000));
        seen.Register(() =>
        {
            delayer.Dispose(); // on the cancel thread: must return, not Join the worker
            gate.Set();
        });

        var cancelThread = new Thread(() => delayer.CancelExecuting());
        cancelThread.Start();
        Assert.True(cancelThread.Join(5000),
            "CancelExecuting deadlocked: Dispose inside the token callback joined the worker");
    }

    [Fact]
    public void Dispose_Of_Another_Defer_From_A_Token_Callback_Should_Wait_For_Its_Delegate()
    {
        // Regression: the self-dispose detection must be scoped to the core whose
        // token is being canceled. It used to skip the worker join for ANY core on
        // a thread running cancellation callbacks, so a Dispose of an unrelated
        // Defer issued from inside this callback returned while the unrelated
        // delegate was still running. That other worker is not blocked on this
        // callback chain: the join must happen, as documented.
        var aInFlight = new ManualResetEventSlim();
        var bInFlight = new ManualResetEventSlim();
        var bDone = new ManualResetEventSlim();
        Defer? b = null;
        b = GetDeferrer(10, (CancellationToken _) =>
        {
            bInFlight.Set();
            Thread.Sleep(600); // ignores its token: only its end can let Dispose return
            bDone.Set();
        });
        using Defer a = GetDeferrer(10, (CancellationToken ct) =>
        {
            // runs on whichever thread cancels A: disposes the unrelated B there
            ct.Register(() => b!.Dispose());
            aInFlight.Set();
            ct.WaitHandle.WaitOne(5000); // released by the cancel below
        });

        _ = b.ExecuteNow(); // B's delegate is the one Dispose must wait out
        Assert.True(bInFlight.Wait(5000)); // B's delegate is mid-run now
        _ = a.Trigger(TimeSpan.FromMilliseconds(10));
        Assert.True(aInFlight.Wait(5000));

        var stopwatch = Stopwatch.StartNew();
        a.CancelExecuting(); // A's callback fires on this thread and disposes B
        stopwatch.Stop();

        Assert.True(bDone.IsSet,
            "the unrelated Defer's Dispose returned while its delegate was still running");
        Assert.True(stopwatch.ElapsedMilliseconds >= 500,
            $"Dispose returned after {stopwatch.ElapsedMilliseconds}ms: it did not join the delegate");
    }

    [Fact]
    public async Task A_Due_Pass_Must_Not_Start_When_Disposed()
    {
        // Regression: TryBeginExecution checks _destroyed under the lock, so a due
        // pass on a disposed core is consumed as canceled instead of running the
        // delegate. Deterministic: the test holds the core's lock while arming a due
        // pass and marking the core destroyed, then invokes TryBeginExecution itself
        // (Monitor is reentrant), so no worker interleaving can occur.
        var ran = 0;
        using Defer delayer = GetDeferrer(0, () => ran++);
        var core = delayer.Core; // internal: visible to this assembly

        var taken = false;
        Monitor.Enter(core._lock, ref taken);
        try
        {
            DeferredExecution execution = delayer.Trigger(TimeSpan.Zero); // arms a due pass; the worker blocks on _lock
            core._destroyed = true; // what Dispose does under the same lock
            var started = core.TryBeginExecution(out _);
            Assert.False(started, "TryBeginExecution started a pass on a disposed core");
            await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
        }
        finally
        {
            Monitor.Exit(core._lock);
        }
        Assert.Equal(0, ran);
    }

    [Fact]
    public void Dispose_While_Running_Should_Not_Pay_The_Idle_Poll()
    {
        // Dispose's wake must not be lost to the sleep-cycle bookkeeping: shutting
        // down takes the remainder of the in-flight delegate, never an extra idle
        // poll on top of it.
        var inFlight = new ManualResetEventSlim();
        using Defer delayer = GetDeferrer(50, () =>
        {
            inFlight.Set();
            Thread.Sleep(500); // ignores the token: Dispose joins us to this
        });

        delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.True(inFlight.Wait(5000));
        var stopwatch = Stopwatch.StartNew();
        delayer.Dispose();
        stopwatch.Stop();

        // the delegate ends ~500ms from here and Dispose must return close on
        // its heels; the failure mode — a lost wake — adds the whole idle poll
        // (1000ms) and lands past every honest bound below
        Assert.True(stopwatch.ElapsedMilliseconds < 1400,
            $"Dispose blocked {stopwatch.ElapsedMilliseconds}ms after the delegate started");
    }

    [Fact]
    public async Task Dispose_From_Inside_Another_Defers_Delegate_Should_Wait_For_That_Delegate()
    {
        // Dispose's self-detection must key on THIS Defer's worker: the ExecutionContext
        // of another Defer's delegate (with all its continuations) is not the worker
        // being disposed, so Dispose must block until that delegate returns.
        var bRunning = new ManualResetEventSlim();
        var bDone = new ManualResetEventSlim();
        var sawBFinishInsideDispose = false;
        Defer? b = null;
        b = Defer.UsingDedicatedThread().ToExecute(_ =>
        {
            bRunning.Set();
            Thread.Sleep(150); // ignores the token: only its end can let Dispose return
            bDone.Set();
        }).ForAtLeast(10).Build();

        using Defer a = GetDeferrer(10, () =>
        {
            _ = b!.Trigger(TimeSpan.FromMilliseconds(10));
            bRunning.Wait(5000); // B's delegate in flight
            b!.Dispose();        // from A's delegate: must wait for B, not self-exit
            sawBFinishInsideDispose = bDone.IsSet;
        });

        await a.Trigger(TimeSpan.FromMilliseconds(10)); // completes only once Dispose returned

        Assert.True(bDone.Wait(5000)); // sanity: B's delegate did run to its end
        Assert.True(sawBFinishInsideDispose); // Dispose blocked on it
    }

    [Fact]
    public async Task Trigger_Token_Should_Be_Canceled_When_Disposed_While_Pending()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(50, () => executionCount++);

        DeferredExecution wait = delayer.Trigger(TimeSpan.FromMilliseconds(5000));
        delayer.Dispose();

        await Assert.ThrowsAsync<TaskCanceledException>(() => wait.AsTask());
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public void Dispose_Should_Be_Idempotent()
    {
        using Defer delayer = GetDeferrer(50, () => { });
        delayer.Dispose();
        delayer.Dispose(); // second dispose must be a no-op
    }

    [Fact]
    public void APIs_Should_Throw_ObjectDisposed_After_Dispose()
    {
        using Defer delayer = GetDeferrer(50, () => { });
        delayer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { delayer.Trigger(); });
        Assert.Throws<ObjectDisposedException>(() => { delayer.ExecuteNow(); });
        Assert.Throws<ObjectDisposedException>(() => delayer.Cancel());
        Assert.Throws<ObjectDisposedException>(() => delayer.CancelPending());
        Assert.Throws<ObjectDisposedException>(() => delayer.CancelExecuting());
        Assert.Throws<ObjectDisposedException>(() =>
        {
            DeferredExecution _ = delayer.Pending;
        });
        Assert.Throws<ObjectDisposedException>(() => delayer.Delay = TimeSpan.FromMilliseconds(1));
        Assert.Throws<ObjectDisposedException>(() => delayer.Pause());
        Assert.Throws<ObjectDisposedException>(() => delayer.Resume());
        Assert.Throws<ObjectDisposedException>(() => delayer.Resume(TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ObjectDisposedException>(() => delayer.TriggerAndWait(TimeSpan.FromMilliseconds(5)));
        Assert.Throws<ObjectDisposedException>(() => { _ = delayer.TriggerAndWaitAsync(); });
        Assert.Throws<ObjectDisposedException>(() => delayer.ExecuteNowAndWait());
        Assert.Throws<ObjectDisposedException>(() => delayer.WaitForNextExecution());
        Assert.Throws<ObjectDisposedException>(() => { _ = delayer.WaitForNextExecutionAsync(); });
    }

    [Fact]
    public void Reading_Configuration_After_Dispose_Should_Be_Harmless()
    {
        // The throwing half of the documented post-dispose contract is pinned by
        // APIs_Should_Throw_ObjectDisposed_After_Dispose; reading the
        // configuration must remain harmless, forever repeatable.
        using Defer delayer = GetDeferrer(200, () => { }, 100, 3);
        delayer.Dispose();

        Assert.NotNull(delayer.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delayer.Delay);
        Assert.Equal(DeferLimit.SpanAndCount, delayer.LimitType);
        Assert.Equal(100u, delayer.MaximumDelaySpan);
        Assert.Equal(3u, delayer.MaximumTriggerLimit);
        Assert.False(delayer.IsPaused);
        Assert.False(delayer.IsPaused); // reads stay repeatable
    }

    [Fact]
    public async Task Abandoned_Defer_Should_Stop_Its_Worker()
    {
        (WeakReference wrapperRef, Thread? workerThread, Task? workerTask) = CreateAbandonedWorker();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Without Dispose, the public wrapper must still be collectable...
        Assert.False(wrapperRef.IsAlive);

        // ...and the worker must notice the abandonment and terminate: "about a
        // second" means at most 1.5s, and that is all this budget may spend
        if (workerThread != null)
        {
            Assert.True(workerThread.Join(1500));
        }
        else
        {
            Task completed = await Task.WhenAny(workerTask!, Task.Delay(1500));
            Assert.Same(workerTask, completed);
        }
    }

    private (WeakReference wrapper, Thread? thread, Task? task) CreateAbandonedWorker()
    {
        Defer delayer = GetDeferrer(500, () => { });
        var thread = delayer.WorkerThread;
        var task = delayer.Core._task; // internal: visible to this assembly
        return (new WeakReference(delayer), thread, task);
    }

    [Fact]
    public async Task A_Run_In_Flight_Should_Finish_When_The_Wrapper_Is_Collected()
    {
        // "a run already in flight always finishes, even if its wrapper is
        // collected while it runs": the abandonment machinery only stops future
        // passes. The in-flight run resolves with its own outcome, the armed
        // successor cycle is canceled by the worker's shutdown.
        var (wrapper, inFlight, release, firstExecution, pendingExecution) = BuildCollectedMidRun();

        Assert.True(inFlight.Wait(5000)); // really mid-run now

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(wrapper.IsAlive); // the wrapper is really gone

        release.Set(); // let the in-flight delegate end
        Assert.True(await Task.WhenAny(firstExecution, Task.Delay(5000)) == firstExecution,
            "the in-flight run did not finish after its wrapper was collected");
        Assert.Equal(TaskStatus.RanToCompletion, firstExecution.Status);

        // the worker then notices the abandonment and shuts down, resolving the
        // still-armed successor cycle as canceled
        Assert.True(await Task.WhenAny(pendingExecution, Task.Delay(2000)) == pendingExecution,
            "the still-armed successor cycle was never resolved");
        Assert.Equal(TaskStatus.Canceled, pendingExecution.Status);
    }

    private (WeakReference wrapper, ManualResetEventSlim inFlight, ManualResetEventSlim release,
        Task firstExecution, Task pendingExecution) BuildCollectedMidRun()
    {
        var inFlight = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        Defer delayer = GetDeferrer(5000, () =>
        {
            inFlight.Set();
            release.Wait(5000); // ends exactly when the test releases it
        });

        Task first = delayer.ExecuteNow().AsTask(); // the pass in flight
        if (!inFlight.Wait(5000))
            throw new TimeoutException("the delegate was not executing within 5s"); // promotion already happened
        Task pending = delayer.Trigger(TimeSpan.FromMilliseconds(5000)).AsTask(); // armed behind it
        return (new WeakReference(delayer), inFlight, release, first, pending);
    }

}

public sealed class DisposeLifetimeTestsUsingTask : DisposeLifetimeTests
{
    protected override bool UsesDedicatedThread => false;
}

public sealed class DisposeLifetimeTestsUsingThread : DisposeLifetimeTests
{
    protected override bool UsesDedicatedThread => true;
}
