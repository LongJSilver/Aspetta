using System;

namespace Some.Restraint;

[Flags()]
public enum DeferLimit
{
    /// <summary>
    /// Allow the delegate execution to be delayed only for a fixed amount of milliseconds (specified by the <see cref="Defer.MaximumDelaySpan"/> property)
    /// AND ALSO for a fixed amount of times (specified by the <see cref="Defer.MaximumTriggerLimit"/> property), 
    /// whichever occurs first.
    /// </summary>
    SpanAndCount = 3,
    /// <summary>
    /// Only allow the delegate execution to be delayed a fixed amount of milliseconds (specified by the <see cref="Defer.MaximumDelaySpan"/> property)
    /// </summary>
    Span = 2,
    /// <summary>
    /// Only allow the delegate execution to be delayed a fixed amount of TIMES (specified by the <see cref="Defer.MaximumTriggerLimit"/> property)
    /// </summary>
    Count = 1,
    /// <summary>
    /// No limit is applied
    /// </summary>
    NoLimit = 0
}

