using System;

namespace Some.Restraint;

/// <summary>
///     Which limits a <see cref="Defer" /> was configured with: none, a maximum delay
///     span, a maximum trigger count, or both.
/// </summary>
[Flags]
public enum DeferLimit
{
    /// <summary>
    ///     No limit is applied.
    /// </summary>
    NoLimit = 0,
    /// <summary>
    ///     The execution is delayed by at most
    ///     <see cref="Defer.MaximumTriggerLimit" /> triggers.
    /// </summary>
    Count = 1,
    /// <summary>
    ///     The execution is delayed by at most the span of
    ///     <see cref="Defer.MaximumDelaySpan" />.
    /// </summary>
    Span = 2,
    /// <summary>
    ///     Both limits active: the execution is delayed by at most the span of
    ///     <see cref="Defer.MaximumDelaySpan" /> and at most
    ///     <see cref="Defer.MaximumTriggerLimit" /> triggers, whichever occurs first.
    /// </summary>
    SpanAndCount = 3

}
