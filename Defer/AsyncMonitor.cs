using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Some.Restraint
{
    internal sealed class AsyncMonitor
    {
        // Internal storage of TaskCompletionSources per lock object
        private static readonly ConditionalWeakTable<object, LockState> _locks =
            new();

        private sealed class LockState
        {
            public readonly object Sync = new();
            public TaskCompletionSource<bool> Tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Wait asynchronously on a lock object until PulseAll is called.
        /// </summary>
        public static Task WaitAsync(object lockObj)
        {
            if (lockObj == null) throw new ArgumentNullException(nameof(lockObj));

            LockState state = _locks.GetOrCreateValue(lockObj);

            lock (state.Sync)
            {
                return state.Tcs.Task;
            }
        }

        /// <summary>
        /// Wait asynchronously on a lock object with timeout and optional cancellation.
        /// Returns true if awakened via PulseAll, false if timeout/canceled.
        /// </summary>
        public static async Task<bool> WaitAsync(object lockObj, TimeSpan timeout, CancellationToken? ct = default)
        {
            if (lockObj == null) throw new ArgumentNullException(nameof(lockObj));

            LockState state = _locks.GetOrCreateValue(lockObj);

            Task OldTask, NewTask;
            lock (state.Sync)
            {
                OldTask = state.Tcs.Task;
                state.Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                NewTask = state.Tcs.Task;
            }
            var delayTask = Task.Delay(ClampToInt32Millis(timeout), ct ?? CancellationToken.None);
            var completed = await Task.WhenAny(OldTask, NewTask, delayTask).ConfigureAwait(false);

            return completed != delayTask;

        }

        private static TimeSpan ClampToInt32Millis(TimeSpan timeout)
        {
            if (timeout == Timeout.InfiniteTimeSpan) return Timeout.InfiniteTimeSpan;
            if (timeout <= TimeSpan.Zero) return TimeSpan.Zero;
            return timeout.TotalMilliseconds > int.MaxValue
                ? TimeSpan.FromMilliseconds(int.MaxValue)
                : timeout;
        }
        /// <summary>
        /// Pulse all waiters for the given lock object.
        /// </summary>
        public static void PulseAll(object lockObj)
        {
            if (lockObj == null) throw new ArgumentNullException(nameof(lockObj));

            LockState state = _locks.GetOrCreateValue(lockObj);

            lock (state.Sync)
            {
                state.Tcs.TrySetResult(true);
            }
        }
    }
}
