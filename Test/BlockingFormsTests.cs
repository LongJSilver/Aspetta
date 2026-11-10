using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     The worker-parameterized \"BlockingFormsTests\" half of the suite: every test runs once against the
///     task-based worker and once against the dedicated thread, through DeferrerTestFactory.
/// </summary>
public abstract class BlockingFormsTests
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
    public void TriggerAndWait_Should_Surface_The_Original_Exception_Unwrapped()
    {
        using Defer delayer = GetDeferrer(50, _ => throw new InvalidOperationException("boom"),
            onErrorHandler: _ => { });

        var ex = Assert.Throws<InvalidOperationException>(
            () => delayer.TriggerAndWait(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
#pragma warning disable xUnit1031 // blocking on the canceled value is the very behavior under test
    public void Blocking_On_A_Canceled_Execution_Should_Throw_TaskCanceledException()
    {
        using Defer delayer = GetDeferrer(50, () => { });
        DeferredExecution pending = delayer.Trigger(TimeSpan.FromMilliseconds(5000));
        delayer.CancelPending();

        Assert.Throws<TaskCanceledException>(() => pending.GetAwaiter().GetResult());
    }
#pragma warning restore xUnit1031

    [Fact]
    public void TriggerAndWait_Extension_Should_Block_Until_Execution()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(100, () => executionCount++);

        delayer.TriggerAndWait(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task TriggerAndWaitAsync_Extension_Should_Wait_For_Execution()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(100, () => executionCount++);

        await delayer.TriggerAndWaitAsync(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void ExecuteNowAndWait_Extension_Should_Block_Until_Execution()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(100, () => executionCount++);

        delayer.ExecuteNowAndWait();

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task WaitForNextExecution_Extension_Should_Return_When_Nothing_Pending()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(100, () => executionCount++);

        await delayer.Trigger(TimeSpan.FromMilliseconds(100));
        delayer.WaitForNextExecution(); // nothing pending: must return without blocking

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public void TriggerAndWait_Extension_Should_Default_To_The_Configured_Delay()
    {
        var executionCount = 0;
        using Defer delayer = GetDeferrer(200, () => executionCount++);

        var stopwatch = Stopwatch.StartNew();
        delayer.TriggerAndWait(); // no interval: the configured 200ms
        stopwatch.Stop();

        Assert.Equal(1, executionCount);
        Assert.True(stopwatch.ElapsedMilliseconds >= 190);
    }

    [Fact]
    public void WaitForNextExecution_Should_Block_While_A_Cycle_Is_Pending()
    {
        var ran = 0;
        using Defer delayer = GetDeferrer(100, () => ran++);

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(100));
        delayer.WaitForNextExecution(); // blocks until the pending cycle has run

        Assert.Equal(1, ran);
    }

}

public sealed class BlockingFormsTestsUsingTask : BlockingFormsTests
{
    protected override bool UsesDedicatedThread => false;
}

public sealed class BlockingFormsTestsUsingThread : BlockingFormsTests
{
    protected override bool UsesDedicatedThread => true;
}
