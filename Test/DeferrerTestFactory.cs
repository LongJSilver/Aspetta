using Some.Restraint;

namespace Aspetta.Tests;

/// <summary>
///     Shared construction for the worker-parameterized test classes: one task-based
///     deferrer and one dedicated-thread deferrer per theme, both through the same
///     builder options.
/// </summary>
internal static class DeferrerTestFactory
{
    public static Defer Build(bool dedicated, uint delayMS, DeferCancellableAction action,
        uint? maximumDelaySpan = null, uint? maximumTriggerLimit = null,
        Action<Exception>? onErrorHandler = null)
    {
        if (dedicated)
        {
            ThreadDeferBuilder builder = Defer.UsingDedicatedThread().ToExecute(action).ForAtLeast(delayMS)
                .Named("TestDelayer_UsingThread");
            if (maximumDelaySpan.HasValue) builder = builder.WithMaximumDelay(maximumDelaySpan.Value);
            if (maximumTriggerLimit.HasValue) builder = builder.WithMaximumTriggerLimit(maximumTriggerLimit.Value);
            if (onErrorHandler != null) builder = builder.WithErrorHandler(onErrorHandler);
            return builder.Build();
        }

        TaskDeferBuilder taskBuilder = Defer.UsingTask().ToExecute(action).ForAtLeast(delayMS)
            .Named("TestDelayer_UsingTask");
        if (maximumDelaySpan.HasValue) taskBuilder = taskBuilder.WithMaximumDelay(maximumDelaySpan.Value);
        if (maximumTriggerLimit.HasValue) taskBuilder = taskBuilder.WithMaximumTriggerLimit(maximumTriggerLimit.Value);
        if (onErrorHandler != null) taskBuilder = taskBuilder.WithErrorHandler(onErrorHandler);
        return taskBuilder.Build();
    }
}
