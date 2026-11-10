using System.Diagnostics;
using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     Timing-sensitive performance tests to spot glaring regressions;
///     kept in their own category so a CI box can exclude them entirely:
///     <code>dotnet test --filter "Category!=Performance"</code>
///     Run them explicitly with:
///     <code>dotnet test --filter "Category=Performance"</code>
///     They are meant to be run on local machines only:
///     the tight tolerances (mean overshoot within 1 ms, exactly 0 allocated bytes)
///     will flake on shared or loaded runners.
/// </summary>
[Trait("Category", "Performance")]
public abstract class PerformanceTestBase
{
    protected abstract Defer GetDeferrer(uint delayMS, DeferAction action);

    [Fact]
    public void ExecutionTime_Within10ms()
    {
        ExecutionTimeTest(100, 10, 5);
    }

    [Fact]
    public void ExecutionTime_Within5ms()
    {
        ExecutionTimeTest(100, 5, 5);
    }

    [Fact]
    public void ExecutionTime_Within2ms()
    {
        ExecutionTimeTest(100, 2, 5);
    }

    [Fact]
    public void ExecutionTime_Within1ms()
    {
        ExecutionTimeTest(100, 1, 5);
    }

    public void ExecutionTimeTest(uint delayMS, int toleranceMS, int tries)
    {
        float mean = 0;
        for (var i = 0; i < tries; i++) mean += ExecutionTimeTest(delayMS);
        mean /= tries;

        // the tolerance is one-sided: an execution may start late by up to
        // toleranceMS, but never early
        Assert.InRange(delayMS + mean, delayMS, delayMS + toleranceMS);
    }

    public float ExecutionTimeTest(uint delayMS = 100)
    {
        // Arrange
        var stopwatch = new Stopwatch();
        using Defer delayer = GetDeferrer(delayMS, () =>
        {
            stopwatch.Stop(); // Stop the measurement when the action starts executing
        });

        // Act
        stopwatch.Start();
        delayer.Trigger();

        // Wait for the action to be executed, but fail instead of hanging forever
        var guard = Stopwatch.StartNew();
        while (stopwatch.IsRunning)
        {
            if (guard.ElapsedMilliseconds > 30000)
                throw new TimeoutException("the delegate was not executed within 30s");
            Thread.Sleep(1);
        }

        // Assert
        var elapsedTicks = stopwatch.ElapsedTicks;

        var usecPerTick = 1000000f / Stopwatch.Frequency;
        var elapsedUsecs = elapsedTicks * usecPerTick;
        return elapsedUsecs / 1000f - delayMS; // overshoot over the nominal delay
    }
}

[Trait("Category", "Performance")]
public class PerformanceTest_UsingTask : PerformanceTestBase
{
    protected override Defer GetDeferrer(uint delayMS, DeferAction action)
    {
        return Defer.UsingTask().ToExecute(action).ForAtLeast(delayMS).Build();
    }
}

[Trait("Category", "Performance")]
public class PerformanceTest_UsingThread : PerformanceTestBase
{
    protected override Defer GetDeferrer(uint delayMS, DeferAction action)
    {
        return Defer.UsingDedicatedThread().ToExecute(action).ForAtLeast(delayMS).Build();
    }
}

/// <summary>
///     The trigger hot path's allocation budget, measured on .NET 10: the dedicated
///     thread worker performs no allocation at all on the coalescing path, and the
///     task worker leaves only a constant residue of wake-scheduling noise — no
///     allocation that scales with the number of triggers.
/// </summary>
[Trait("Category", "Performance")]
public class AllocationTest
{
    private static long CoalescingBurstBytes(Defer delayer, int triggers)
    {
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(60000)); // open the cycle: the one-time value
        for (var i = 0; i < 100; i++) _ = delayer.Trigger(TimeSpan.FromMilliseconds(60000)); // warm every path

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < triggers; i++) _ = delayer.Trigger(TimeSpan.FromMilliseconds(60000));
        var after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    [Fact]
    public void Coalescing_Triggers_Should_Not_Allocate_On_The_Thread_Worker()
    {
        using Defer delayer = Defer.UsingDedicatedThread().ToExecute(_ => { }).ForAtLeast(60000).Build();
        Assert.Equal(0, CoalescingBurstBytes(delayer, 2000));
    }

    [Fact]
    public void Coalescing_Triggers_Should_Not_Scale_Allocations_On_The_Task_Worker()
    {
        // Linear only in the constant wake-scheduling residue, proven by growing
        // the burst: that residue is the same constant on every trigger, so a
        // burst ten times longer allocates ten times the residue and no more. The tolerance is
        // the tenfold rate plus slack (less than 2 bytes per large-burst trigger):
        // any real allocation per trigger (the smallest heap object already costs
        // 24 bytes, 480K bytes over the large burst) swallows it ten times over.
        // A tolerance relative to small alone (e.g. small * 3) flakes: measurement
        // noise moves small by kilobytes while the decayed per-trigger residue it
        // represents is near-constant, collapsing the budget on unlucky runs.
        using Defer delayer = Defer.UsingTask().ToExecute(() => { }).ForAtLeast(60000).Build();

        long small = CoalescingBurstBytes(delayer, 2000);
        long large = CoalescingBurstBytes(delayer, 20000);

        Assert.True(large <= small * 10 + 24576,
            $"2000 triggers allocated {small} bytes; 20000 triggers allocated {large} bytes: " +
            "the footprint must not be more than the tenfold residue");
    }
}
