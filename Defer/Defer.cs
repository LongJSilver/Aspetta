using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;

namespace Some.Restraint;

public class Defer : IDisposable
{
    private const ushort ABSOLUTE_MINIMUM_DELAY = 5;

    // ************ PUBLIC Builder API *********** //

    public static DeferBuilder ToExecute(Action act) => UsingTask().ToExecute(act);
    public static DeferBuilder UsingTask(TaskFactory? t = null) => new DeferBuilder(t ?? Task.Factory);
    public static DeferBuilder UsingDedicatedThread() => new DeferBuilder(null);

    // ************ INTERNAL MACHINERY *********** //

    private readonly Thread? _thread;
    private readonly Task? _task;

    private readonly Action _exec;
    private readonly Action<Exception>? _doOnError;

    private readonly Stopwatch _timer;
    private readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);
    private readonly object _waitingObjectsMonitor;

    private readonly object _lock;

    // ****************** STATE ****************** //

    private TimeSpan _delay;
    private uint _executionCount;
    private int _debouncedEvents;
    private long _nextExecution = Int64.MaxValue;
    private long _executionLimit = Int64.MaxValue;
    private bool _canceled;
    private bool _paused;
    private volatile bool _destroyed;

    // ****************** PUBLIC API ****************** //

    public string? Name { get; private set; }

    public TimeSpan Delay
    {
        get => _delay;
        set
        {
            if (value.TotalMilliseconds < ABSOLUTE_MINIMUM_DELAY)
                value = TimeSpan.FromMilliseconds(ABSOLUTE_MINIMUM_DELAY);
            _delay = value;
        }
    }

    /// <summary>
    /// The maximum amount of milliseconds this timer will be allowed to defer execution. 
    /// After that time span has passed, the delegate action is triggered regardless of the normal delay mechanism.
    /// </summary>
    /// <returns></returns>
    public uint? MaximumDelaySpan { get; internal set; }

    /// <summary>
    /// The maximum amount of times this timer will be allowed to defer execution.
    /// </summary>
    public uint? MaximumTriggerLimit { get; internal set; }

    public DeferLimit LimitType
    {
        get
        {
            DeferLimit result = DeferLimit.NoLimit;
            if (MaximumDelaySpan.HasValue)
            {
                result |= DeferLimit.Span;
            }

            if (MaximumTriggerLimit.HasValue)
            {
                result |= DeferLimit.Count;
            }
            return result;
        }
    }

    public bool IsPaused
    {
        get
        {
            bool result = false;
            lock (this._lock)
            {
                result = this._paused;
            }

            return result;
        }
        set
        {
            lock (this._lock)
            {
                bool old = _paused;
                this._paused = value;
                if (!value && old) Trigger();
            }
        }
    }

    public void CancelPending()
    {
        if (_destroyed)
            throw new ObjectDisposedException(nameof(Defer));
        lock (this._lock)
        {
            if (this._debouncedEvents > 0)
            {
                this._canceled = true;
                _signal.Set();
            }
        }
    }

    [DebuggerStepThrough]
    public void Trigger(int overrideInterval) => Trigger((uint)Math.Max(overrideInterval, ABSOLUTE_MINIMUM_DELAY));
    [DebuggerStepThrough]
    public void Trigger(uint overrideInterval = ABSOLUTE_MINIMUM_DELAY) => Trigger(TimeSpan.FromMilliseconds(overrideInterval));
    public void Trigger(TimeSpan? overrideInterval)
    {
        if (_destroyed)
            throw new ObjectDisposedException(nameof(Defer));

        if (overrideInterval == null) overrideInterval = this._delay;
        if (overrideInterval.HasValue && overrideInterval.Value.TotalMilliseconds < ABSOLUTE_MINIMUM_DELAY)
            overrideInterval = TimeSpan.FromMilliseconds(ABSOLUTE_MINIMUM_DELAY);

        lock (this._lock)
        {
            this._canceled = false;

            this._debouncedEvents += 1;
            long now = _timer.ElapsedMilliseconds;

            this._nextExecution = now + (long)overrideInterval.Value.TotalMilliseconds;

            if (MaximumTriggerLimit.HasValue && (this._debouncedEvents >= this.MaximumTriggerLimit))
            {
                //'we have a count limit 
                this._nextExecution = now;
            }

            if (this.MaximumDelaySpan.HasValue)
            {
                //'we have a span limit
                this._executionLimit = Math.Min(now + this.MaximumDelaySpan.Value, this._executionLimit);
                this._nextExecution = Math.Min(this._executionLimit, this._nextExecution);
            }

            if (this._debouncedEvents > 0)
            {
                _signal.Set();
            }
        }
    }

    [DebuggerStepThrough]
    public void TriggerAndWait(int overrideInterval) => TriggerAndWait((uint)Math.Max(overrideInterval, ABSOLUTE_MINIMUM_DELAY));
    [DebuggerStepThrough]
    public void TriggerAndWait(uint interval = ABSOLUTE_MINIMUM_DELAY) => TriggerAndWait(TimeSpan.FromMilliseconds(interval));
    [DebuggerStepThrough]
    public void TriggerAndWait(TimeSpan span) => TriggerAndWaitAsync(span).Wait();

    [DebuggerStepThrough]
    public Task TriggerAndWaitAsync(int overrideInterval) => TriggerAndWaitAsync((uint)Math.Max(overrideInterval, ABSOLUTE_MINIMUM_DELAY));
    [DebuggerStepThrough]
    public Task TriggerAndWaitAsync(uint interval = ABSOLUTE_MINIMUM_DELAY) => TriggerAndWaitAsync(TimeSpan.FromMilliseconds(interval));
    public async Task TriggerAndWaitAsync(TimeSpan interval)
    {
        if (_destroyed) return;
        uint current = _executionCount;
        Trigger(interval);
        await Task.Run(() =>
        {
            lock (_waitingObjectsMonitor)
            {
                while (current == _executionCount)
                {
                    Monitor.Wait(this._waitingObjectsMonitor);
                }
            }
        }
         );
    }

    public void ExecuteNow()
    {
        if (_destroyed)
            throw new ObjectDisposedException(nameof(Defer));
        lock (this._lock)
        {
            this._canceled = false;
            this._debouncedEvents += 1;
            this._nextExecution = _timer.ElapsedMilliseconds;
            _signal.Set();
        }
    }

    public void ExecuteNowAndWait() => ExecuteNowAndWaitAsync().Wait();
    public async Task ExecuteNowAndWaitAsync()
    {
        if (_destroyed) return;
        UInt32 current = _executionCount;
        this.ExecuteNow();
        await Task.Run(() =>
        {
            lock (_waitingObjectsMonitor)
            {
                while (current == _executionCount)
                {
                    Monitor.Wait(this._waitingObjectsMonitor);
                }
            }
        }
         );
    }

    public void WaitForNextExecution() => WaitForNextExecutionAsync().Wait();

    public async Task WaitForNextExecutionAsync()
    {
        if (_destroyed) return;
        uint current = _executionCount;
        await Task.Run(() =>
        {
            lock (_waitingObjectsMonitor)
            {
                while (current == _executionCount)
                {
                    Monitor.Wait(this._waitingObjectsMonitor);
                }
            }
        }
         );
    }


    // ****************** INTERNAL WORK ****************** //

    internal Defer(string? name, TimeSpan delay, Action exec, TaskFactory? taskFactory, Action<Exception>? onError = null)
    {
        this.Name = name;
        this._delay = delay;
        this._exec = exec;
        this._doOnError = onError;
        _timer = Stopwatch.StartNew();
        _lock = new object();
        _waitingObjectsMonitor = new object();

        //If we get no task factory, then we are meant to use a dedecated thread
        if (taskFactory == null)
        {
            this._task = null;

            this._thread = new Thread(this.Loop)
            {
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            this._thread.Start();
        }
        else
        {
            this._task = taskFactory.StartNew(() => Loop(), TaskCreationOptions.LongRunning);

            this._thread = null;
        }
    }

    private void Loop()
    {
        while (!this._destroyed)
        {
            long now = _timer.ElapsedMilliseconds;
            bool execute = false;


            lock (this._lock)
            {
                if (now >= this._nextExecution)
                {
                    //should execute?
                    execute = !this._canceled & !this._paused;
                    // setup long sleep time
                    this._canceled = false;
                    this._nextExecution = Int64.MaxValue;
                    this._executionLimit = Int64.MaxValue;
                    this._debouncedEvents = 0;
                }
                else
                {
                    // should not execute, 
                    // should not change next exec time
                    execute = false;
                }
            }

            if (execute)
            {
                try
                {
                    this._exec();
                    _executionCount++;

                    lock (this._waitingObjectsMonitor)
                    {
                        Monitor.PulseAll(this._waitingObjectsMonitor);
                    }
                }
                catch (Exception ex)
                {
                    this._doOnError?.Invoke(ex);
                }
            }

            long sleepFor = 0;
            _signal.Reset();
            lock (this._lock)
            {
                now = _timer.ElapsedMilliseconds;
                sleepFor = Math.Min(Int32.MaxValue, this._nextExecution - now);
            }
            sleepFor = (Math.Max(sleepFor, 1));
            _signal.Wait(TimeSpan.FromMilliseconds(sleepFor));
        }
        // we are shutting down
        lock (this._waitingObjectsMonitor)
        {
            Monitor.PulseAll(this._waitingObjectsMonitor);
        }
    }

    /// <summary>
    /// Utility function to try and obtain a more accurate wait time.
    /// Currently unused.
    /// </summary>
    /// <param name="ms"></param>
    private void WakeAwait(long ms)
    {
        long startAt = _timer.ElapsedMilliseconds;
        while (!_signal.IsSet && startAt + ms > _timer.ElapsedMilliseconds)
        {
            try
            {
                Thread.Sleep(TimeSpan.FromTicks(500));
            }
            catch (Exception)
            {

            }
        }
    }

    public void Dispose()
    {
        if (_destroyed) return;
        lock (this._lock)
        {
            this._destroyed = true;
            _signal.Set();
        }

        if (_thread != null)
        {
            _thread.Join();
        }
        else if (_task != null)
        {
            _task.Wait();
            _task.Dispose();
        }

        _signal.Dispose();
        GC.SuppressFinalize(this);
    }
}

