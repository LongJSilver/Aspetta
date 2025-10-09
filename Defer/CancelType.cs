using System;

namespace Some.Restraint
{
    [Flags]
    public enum CancelType
    {
        CurrentlyExecuting = 1,
        PendingExecution = 2,
        All = CurrentlyExecuting | PendingExecution
    }
}
