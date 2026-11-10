using Some.Restraint;

namespace Aspetta.Tests;

public class AsyncDelegateTest
{
    [Fact]
    public async Task Async_Delegate_Should_Complete_The_Execution()
    {
        var executionCount = 0;
        using Defer delayer = Defer.UsingTask()
            .ToExecute(async () =>
            {
                await Task.Delay(50);
                executionCount++;
            })
            .ForAtLeast(50)
            .Build();

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Assert.False(execution.IsCompleted); // the delegate is awaiting
        await execution;

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task Async_Delegate_Fault_Should_Propagate_Through_The_Execution()
    {
        using Defer delayer = Defer.UsingTask()
            .ToExecute(async () =>
            {
                await Task.Delay(20);
                throw new InvalidOperationException("boom");
            })
            .ForAtLeast(20)
            .WithErrorHandler(_ => { })
            .Build();

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(20));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => execution.AsTask());
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Cancellable_Async_Delegate_Should_Cancel_The_Execution()
    {
        using Defer delayer = Defer.UsingTask()
            .ToExecute(async ct => await Task.Delay(60000, ct))
            .ForAtLeast(50)
            .Build();

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await Task.Delay(100); // the delegate is now in flight
        delayer.CancelExecuting();

        await Assert.ThrowsAsync<TaskCanceledException>(() => execution.AsTask());
    }

    [Fact]
    public async Task Sync_Delegates_On_The_Task_Worker_Should_Behave_As_Before()
    {
        var executionCount = 0;
        using Defer delayer = Defer.UsingTask()
            .ToExecute(() => executionCount++)
            .ForAtLeast(50)
            .Build();

        await delayer.Trigger(TimeSpan.FromMilliseconds(50));
        await delayer.ExecuteNow();

        Assert.Equal(2, executionCount);
    }

    [Fact]
    public async Task Dispose_From_Inside_Async_Delegate_Should_Not_Deadlock()
    {
        Defer? delayer = null;
        delayer = Defer.UsingTask()
            .ToExecute(async () =>
            {
                await Task.Delay(10);
                delayer!.Dispose(); // after an await: runs on the delegate's own task
            })
            .ForAtLeast(50)
            .Build();

        DeferredExecution execution = delayer.Trigger(TimeSpan.FromMilliseconds(50));
        Task task = execution.AsTask();
        Assert.True(await Task.WhenAny(task, Task.Delay(5000)) == task,
            "the in-flight execution was not resolved after a self-dispose");
    }
}
