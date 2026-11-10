using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     The worker-parameterized \"LimitsTests\" half of the suite: every test runs once against the
///     task-based worker and once against the dedicated thread, through DeferrerTestFactory.
/// </summary>
public abstract class LimitsTests
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
    public async Task Delayer_Should_Respect_MaximumDelaySpan()
    {
        // Arrange: every trigger pushes the deadline 2s away, so without the
        // 300ms span cap no execution could ever happen inside the loop below
        var executionCount = 0;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(2000, () => executionCount++, 300);

        // Act: keep debouncing well past the span
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(2000));
        for (var guard = 0; guard < 40 && !execution.IsCompleted; guard++)
        {
            await Task.Delay(100);
            _ = delayer.Trigger(TimeSpan.FromMilliseconds(2000));
        }

        await execution;
        var elapsed = stopwatch.ElapsedMilliseconds;

        // Assert: capped well below the configured 2000ms delay
        Assert.Equal(1, executionCount);
        Assert.True(elapsed < 1500, $"executed after {elapsed}ms");
    }

    [Fact]
    public async Task Delayer_Should_Respect_MaximumDelayCount()
    {
        // Arrange: the 2000ms delay would push the deadline far ahead; the third
        // trigger must reach the count limit and force execution immediately
        var executionCount = 0;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(2000, () => executionCount++, maximumTriggerLimit: 3);

        // Act
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(2000));
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(2000));
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(2000)); // the limit forces this one now
        await execution;
        var elapsed = stopwatch.ElapsedMilliseconds;

        // Assert: fired without waiting the configured 2000ms delay
        Assert.Equal(1, executionCount);
        Assert.True(elapsed < 1000, $"executed after {elapsed}ms");
    }

    [Fact]
    public async Task A_VaryingInterval_Burst_Should_Still_Be_Capped_By_The_Maximum_Delay_Span()
    {
        // The span anchors to the burst's first trigger: per-trigger overrides of
        // different sizes keep pushing the normal deadline away, and the cap must
        // still deliver the execution at the span.
        var ran = 0;
        var ranAtMs = -1L;
        var stopwatch = Stopwatch.StartNew();
        using Defer delayer = GetDeferrer(5000, () =>
        {
            ran++;
            ranAtMs = stopwatch.ElapsedMilliseconds;
        }, maximumDelaySpan: 300);

        var intervals = new[] { 4000u, 3000u, 5000u, 3500u, 4500u, 3200u, 5000u };
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(intervals[0]));
        for (var i = 1; i < intervals.Length && !execution.IsCompleted; i++)
        {
            await Task.Delay(40);
            execution = delayer.Trigger(TimeSpan.FromMilliseconds(intervals[i]));
        }

        await execution;
        Assert.Equal(1, ran);
        // the burst's maximum span (~300ms), not the configured 5000ms, and not
        // any of the pushed-away deadlines (~seconds away)
        Assert.InRange(ranAtMs, 250, 390);
    }

    [Fact]
    public void LimitType_Should_Reflect_The_Configured_Limits()
    {
        using Defer unlimited = GetDeferrer(50, () => { });
        using Defer limited = GetDeferrer(50, () => { }, 200, 3);
        using Defer spanOnly = GetDeferrer(50, () => { }, 200);
        using Defer countOnly = GetDeferrer(50, () => { }, maximumTriggerLimit: 3);

        Assert.Equal(DeferLimit.NoLimit, unlimited.LimitType);
        Assert.Equal(DeferLimit.SpanAndCount, limited.LimitType);
        Assert.Equal(DeferLimit.Span, spanOnly.LimitType);
        Assert.Equal(DeferLimit.Count, countOnly.LimitType);
    }

    [Fact]
    public async Task Trigger_Limit_Of_One_Should_Be_A_Pass_Through()
    {
        // Degenerate limit: every trigger reaches the count limit and fires now.
        var ran = 0;
        using Defer delayer = GetDeferrer(500, () => ran++, maximumTriggerLimit: 1);

        var sw = Stopwatch.StartNew();
        await delayer.Trigger(); // the very first trigger hits the limit
        Assert.True(sw.ElapsedMilliseconds < 450, $"executed after {sw.ElapsedMilliseconds}ms");

        await delayer.Trigger();
        Assert.Equal(2, ran);
    }

    [Fact]
    public async Task Zero_Span_Should_Be_A_Pass_Through()
    {
        // Degenerate span: the deadline expires the moment the cycle opens.
        var ran = 0;
        using Defer delayer = GetDeferrer(500, () => ran++, 0);

        var sw = Stopwatch.StartNew();
        await delayer.Trigger();
        Assert.True(sw.ElapsedMilliseconds < 450, $"executed after {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task The_Count_Limit_Should_Win_When_It_Comes_First()
    {
        // Both limits configured: the trigger count is reached long before the
        // span can expire, so the count governs.
        var ran = 0;
        using Defer delayer = GetDeferrer(3000, () => ran++, 600, 2);

        var sw = Stopwatch.StartNew();
        _ = delayer.Trigger(); // count 1, span would fire at ~600ms
        await delayer.Trigger(); // count 2: fires now, before the span

        Assert.True(sw.ElapsedMilliseconds < 400, $"executed after {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task The_Span_Limit_Should_Win_When_It_Comes_First()
    {
        // Both limits configured and the count never reached: the span governs.
        var ran = 0;
        using Defer delayer = GetDeferrer(3000, () => ran++, 400, 5);

        var sw = Stopwatch.StartNew();
        await delayer.Trigger(); // deadline capped at ~400ms by the span

        Assert.True(sw.ElapsedMilliseconds < 1500, $"executed after {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, ran);
    }

}

public sealed class LimitsTestsUsingTask : LimitsTests
{
    protected override bool UsesDedicatedThread => false;
}

public sealed class LimitsTestsUsingThread : LimitsTests
{
    protected override bool UsesDedicatedThread => true;
}
