using System;
using System.Threading;

namespace Some.Restraint;

public delegate void DeferAction();
public delegate void DeferCancellableAction(CancellationToken cancel);
public class CancellableActionAdapter
{
    private DeferAction _simpleAction;
    public CancellableActionAdapter(DeferAction simpleAction)
    {
        _simpleAction = simpleAction ?? throw new ArgumentNullException(nameof(simpleAction));
    }
    public void Execute(CancellationToken _)
    {
        _simpleAction();
    }
}
