using System.Diagnostics;
using System.Reflection;
using Some.Restraint;

namespace Aspetta.Tests;

public class DebuggerMetadataTest
{
    private static void TheNamedDelegate(CancellationToken _)
    {
    }

    private static string SummaryOf(Defer defer)
    {
        return defer.DebuggerSummary; // internal: visible to this assembly
    }

    [Fact]
    public void Defer_Should_Carry_DebuggerDisplay_And_TypeProxy()
    {
        Assert.NotNull(typeof(Defer).GetCustomAttribute<DebuggerDisplayAttribute>());
        Assert.NotNull(typeof(Defer).GetCustomAttribute<DebuggerTypeProxyAttribute>());
    }

    [Fact]
    public void DebuggerSummary_Should_Name_The_Bound_Delegate()
    {
        using Defer taskWorker =
            Defer.UsingTask().ToExecute(TheNamedDelegate).ForAtLeast(100).Named("debugged").Build();
        using Defer threadWorker = Defer.UsingDedicatedThread().ToExecute(TheNamedDelegate).ForAtLeast(100).Build();

        Assert.Contains("TheNamedDelegate", SummaryOf(taskWorker));
        Assert.Contains("debugged", SummaryOf(taskWorker));
        Assert.Contains("TheNamedDelegate", SummaryOf(threadWorker));
    }

    [Fact]
    public async Task DebuggerSummary_Should_Report_The_Live_State()
    {
        var executions = 0;
        using Defer delayer = Defer.UsingTask()
            .ToExecute(() => executions++)
            .ForAtLeast(50)
            .Build();

        Assert.Contains("idle", SummaryOf(delayer));
        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(500));
        Assert.Contains("armed", SummaryOf(delayer));
        delayer.CancelPending();
        Assert.Contains("idle", SummaryOf(delayer));
        try
        {
            await execution;
        }
        catch (OperationCanceledException)
        {
        } // canceled pending: expected here
    }

    [Fact]
    public void DebuggerSummary_Should_Report_The_Armed_Trigger_Count()
    {
        var executions = 0;
        using Defer delayer = Defer.UsingTask()
            .ToExecute(() => executions++)
            .ForAtLeast(500)
            .Build();

        _ = delayer.Trigger(TimeSpan.FromMilliseconds(500));
        _ = delayer.Trigger(TimeSpan.FromMilliseconds(500)); // coalescing

        Assert.Contains("armed (2 triggers)", SummaryOf(delayer));
    }
}

// ************** Trigger provenance (any build: tracing is a runtime option) ************** //

public class TriggerTracingTest
{
    [Fact]
    public void WithoutTriggerTracing_No_Provenance_Is_Captured()
    {
        using Defer delayer = Defer.UsingTask()
            .ToExecute(() => { })
            .ForAtLeast(500)
            .Build();

        delayer.Trigger(TimeSpan.FromMilliseconds(500));

        Assert.Null(delayer.Core.LastTriggerStackTrace);
        Assert.Null(delayer.Core.CycleOpeningStackTrace);
    }

    private static void ArmCycle(Defer delayer)
    {
        delayer.Trigger(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void WithTriggerTracing_Should_Capture_Trigger_Provenance()
    {
        using Defer delayer = Defer.UsingTask()
            .ToExecute(() => { })
            .ForAtLeast(500)
            .WithTriggerTracing()
            .Build();

        ArmCycle(delayer); // the opening trigger, from its own helper frame
        var opening = delayer.Core.CycleOpeningStackTrace;
        Assert.NotNull(opening);
        Assert.Contains(nameof(ArmCycle), opening);

        // a coalescing trigger updates the last-trigger stack but not the cycle opener
        delayer.Trigger(TimeSpan.FromMilliseconds(500)); // a shallower frame than ArmCycle
        Assert.NotNull(delayer.Core.LastTriggerStackTrace);
        Assert.Contains(nameof(ArmCycle), delayer.Core.CycleOpeningStackTrace!); // opener kept
    }

    [Fact]
    public async Task Traced_Run_Should_Fossilize_The_Realized_Debounce()
    {
        using Defer delayer = Defer.UsingTask()
            .ToExecute(() => { })
            .ForAtLeast(50)
            .WithTriggerTracing()
            .Build();

        await delayer.Trigger(TimeSpan.FromMilliseconds(300));

        // the pass started, after the cycle opened, and both are recorded. The
        // recorded openings are legitimately 0 when the whole cycle opened within
        // the first millisecond of the core's clock, so assert the fossilized
        // span, not the epoch. Upper bound too: a fossilization bug that offsets
        // both stamps by the same amount cannot hide behind a lower-only check.
        var passStarted = delayer.Core.PassStartedAtMs;
        var openedAt = delayer.Core.CycleOpeningAtMs;
        Assert.InRange(passStarted - openedAt, 250, 1000); // opening trigger armed 300ms, run no earlier and not far later
    }
}
