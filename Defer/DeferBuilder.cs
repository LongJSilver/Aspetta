using System;
using System.Threading.Tasks;

namespace Some.Restraint;

public class DeferBuilder
{
    private string? _name;
    private TimeSpan? _delay;
    private uint? _maximumDelaySpan;
    private uint? _maximumTriggerCount;
    private DeferCancellableAction? _cancelableExec;
    private Action<Exception>? _onError;
    private readonly TaskFactory? _taskFactory;

    internal DeferBuilder(TaskFactory? t)
    {
        _taskFactory = t;
    }

    public DeferBuilder Named(string name)
    {
        _name = name;
        return this;
    }

    public DeferBuilder ForAtLeast(uint delayMilliseconds)
    {
        _delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        return this;
    }

    public DeferBuilder ForAtLeast(TimeSpan delay)
    {
        _delay = delay;
        return this;
    }

    public DeferBuilder WithMaximumDelay(uint maximumDelaySpan)
    {
        _maximumDelaySpan = maximumDelaySpan;
        return this;
    }

    public DeferBuilder WithMaximumTriggerLimit(uint maximumTriggerCount)
    {
        _maximumTriggerCount = maximumTriggerCount;
        return this;
    }

    public DeferBuilder ToExecute(DeferAction exec)
    {
        _cancelableExec = new CancellableActionAdapter(exec).Execute;
        return this;
    }

    public DeferBuilder ToExecute(DeferCancellableAction exec)
    {
        _cancelableExec = exec;
        return this;
    }

    public DeferBuilder WithErrorHandler(Action<Exception> onError)
    {
        _onError = onError;
        return this;
    }

    public Defer Build()
    {
        if (_cancelableExec == null)
            throw new InvalidOperationException("Execution action must be specified.");
        if (_delay == null)
            throw new InvalidOperationException("A positive delay must be specified.");

        var defer = new Defer(
            _name,
            _delay.Value,
            _cancelableExec,
            _taskFactory,
            _onError
        );

        defer.MaximumDelaySpan = _maximumDelaySpan;
        defer.MaximumTriggerLimit = _maximumTriggerCount;

        return defer;
    }
}

