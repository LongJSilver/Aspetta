using System.Reflection;
using Some.Restraint;

namespace Aspetta.Tests;

public class BuilderApiTest
{
    [Fact]
    public async Task The_Default_ToExecute_Shorthand_Should_Build_A_Task_Worker()
    {
        // The README's headline form: ToExecute without an explicit Using* call.
        var ran = 0;
        using Defer delayer = Defer.ToExecute(() => ran++).ForAtLeast(50).Build();

        await delayer.Trigger(TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, ran);
    }

    [Fact]
    public void ToExecute_With_A_Null_Delegate_Should_Throw_ArgumentNullException_At_Build_Time()
    {
        Assert.Throws<ArgumentNullException>(() => Defer.ToExecute((DeferAction)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.ToExecute((DeferCancellableAction)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.ToExecute((Func<Task>)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.ToExecute((Func<CancellationToken, Task>)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingTask().ToExecute((DeferAction)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingTask().ToExecute((Func<Task>)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingDedicatedThread().ToExecute((DeferAction)null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingDedicatedThread().ToExecute((DeferCancellableAction)null!));
    }

    [Fact]
    public void Build_Without_ForAtLeast_Should_Point_At_ForAtLeast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Defer.ToExecute(() => { }).Build());
        Assert.Contains("ForAtLeast", ex.Message);
    }

    [Fact]
    public void Named_Should_Set_The_Runtime_Name()
    {
        using Defer delayer = Defer.ToExecute((DeferAction)(() => { }))
            .ForAtLeast(10)
            .Named("bob")
            .Build();

        Assert.Equal("bob", delayer.Name);
    }

    [Fact]
    public void Named_And_WithErrorHandler_Should_Reject_Null()
    {
        Assert.Throws<ArgumentNullException>(() => Defer.UsingTask().Named(null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingDedicatedThread().Named(null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingTask().WithErrorHandler(null!));
        Assert.Throws<ArgumentNullException>(() => Defer.UsingDedicatedThread().WithErrorHandler(null!));
    }

    [Fact]
    public void WorkerThread_Should_Exist_Only_For_The_Dedicated_Thread_Worker()
    {
        using Defer taskWorker = Defer.UsingTask().ToExecute(() => { }).ForAtLeast(10).Build();
        using Defer threadWorker = Defer.UsingDedicatedThread().ToExecute(() => { }).ForAtLeast(10).Build();

        Assert.Null(taskWorker.WorkerThread);
        Assert.NotNull(threadWorker.WorkerThread);
        Assert.True(threadWorker.WorkerThread!.IsAlive);
    }

    /// <summary>
    ///     Counts the tasks scheduled through it and executes them on the thread
    ///     pool: enough to observe whether a TaskFactory pinned to it is honored.
    /// </summary>
    private sealed class RecordingTaskScheduler : TaskScheduler
    {
        private int _scheduledCount;
        public int ScheduledCount => Volatile.Read(ref _scheduledCount);

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _scheduledCount);
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => Array.Empty<Task>();
    }

    [Fact]
    public async Task UsingTask_With_A_Factory_Should_Schedule_The_Worker_Through_It()
    {
        var scheduler = new RecordingTaskScheduler();
        using Defer delayer = Defer.UsingTask(new TaskFactory(scheduler))
            .ToExecute(() => { })
            .ForAtLeast(10)
            .Build();

        await delayer.Trigger(TimeSpan.FromMilliseconds(10));

        Assert.True(scheduler.ScheduledCount >= 1,
            "the worker loop was never scheduled through the custom scheduler");
    }

    [Fact]
    public void UsingTask_Without_A_Factory_Should_Ignore_The_Ambient_Scheduler()
    {
        // Building inside a task running on a custom scheduler must not capture
        // that scheduler: the worker is pinned to TaskScheduler.Default. The
        // harness action is fully synchronous so that only the harness task
        // itself (and nothing of the Defer, if the pinning works) can ever be
        // scheduled through it: an async harness would reschedule its own
        // continuation through this scheduler and muddy the count.
        var scheduler = new RecordingTaskScheduler();
        var done = new ManualResetEventSlim();
        var ran = 0;
        Task.Factory.StartNew(() =>
        {
            using Defer delayer = Defer.UsingTask()
                .ToExecute(() => Interlocked.Increment(ref ran))
                .ForAtLeast(10)
                .Build();
            delayer.TriggerAndWait(TimeSpan.FromMilliseconds(10)); // blocks until the run completes
            done.Set();
        }, CancellationToken.None, TaskCreationOptions.None, scheduler);

        Assert.True(done.Wait(5000), "the harness action did not finish");
        Assert.Equal(1, ran);
        // exactly the harness task itself: had the worker captured the ambient
        // scheduler, its loop would have been scheduled through it too
        Assert.Equal(1, scheduler.ScheduledCount);
    }

    [Fact]
    public void Async_Void_Delegates_Should_Be_Rejected_At_Build_Time()
    {
        // An async lambda converts silently to a void delegate (async void); the
        // builder must refuse it instead of letting a worker await behind its back.
        Assert.Throws<InvalidOperationException>(() =>
            Defer.UsingDedicatedThread().ToExecute((DeferAction)(async () => await Task.Delay(10))));
        Assert.Throws<InvalidOperationException>(() =>
            Defer.UsingTask().ToExecute((DeferAction)(async () => await Task.Delay(10))));
    }
}
