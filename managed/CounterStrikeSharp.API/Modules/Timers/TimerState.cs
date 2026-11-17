using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Modules.Timers;

internal interface ITimerBackend
{
    IntPtr Create(float interval, FunctionReference callback, int flags);
    void Kill(IntPtr handle);
}
internal sealed class NativeTimerBackend : ITimerBackend
{
    internal static readonly NativeTimerBackend Instance = new();
    public IntPtr Create(float interval, FunctionReference callback, int flags) => NativeAPI.CreateTimer(interval, callback, flags);
    public void Kill(IntPtr handle) => NativeAPI.KillTimer(handle);
}

// The native callback roots this API-owned state, not the user's delegate after
// cancellation. Each timer has an independent reference even for equal Actions.
internal sealed class TimerState
{
    private static readonly object ActiveSync = new();
    private static readonly HashSet<TimerState> Active = new();
    private readonly object _sync = new();
    private readonly ITimerBackend _backend;
    private readonly TimerFlags _flags;
    private Action? _callback;
    private FunctionReference? _reference;
    private WorkOwnership.Registration? _ownership;
    internal Action? Finished;
    internal IntPtr Handle { get; private set; }

    internal TimerState(float interval, Action callback, TimerFlags flags, ITimerBackend backend)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!float.IsFinite(interval) || interval < 0) throw new ArgumentOutOfRangeException(nameof(interval));
        _backend = backend; _flags = flags; _callback = callback;
        lock (_sync)
        {
            _ownership = new WorkOwnership.Registration(callback, CancelOwned);
            if (_ownership.Rejected) throw new InvalidOperationException("Cannot create a timer for an unloaded plugin.");
            _reference = FunctionReference.CreateUnique((Action)Invoke);
            try
            {
                Handle = backend.Create(interval, _reference, (int)flags);
                if (Handle == IntPtr.Zero) throw new InvalidOperationException("Native timer creation failed.");
                lock (ActiveSync) Active.Add(this);
            }
            catch { Retire(); throw; }
        }
    }

    private void Invoke()
    {
        Action? callback;
        lock (_sync) callback = _callback;
        var started = RuntimeDiagnostics.Begin();
        try { callback?.Invoke(); }
        finally
        {
            if (callback != null && started != 0) RuntimeDiagnostics.End(callback, started, "timer");
            if ((_flags & TimerFlags.REPEAT) == 0) Retire();
        }
    }

    private void CancelOwned()
    {
        lock (_sync)
        {
            if (Handle == IntPtr.Zero) return;
            _callback = null;
        }
        // ALC unloading may originate on a worker. Native removal must happen on
        // the game thread; the now-inert native trampoline remains rooted until then.
        Server.EnqueueCleanup(Stop);
    }

    internal void Stop()
    {
        lock (_sync)
        {
            if (Handle == IntPtr.Zero) return;
            _callback = null;
            _backend.Kill(Handle); // Retain the trampoline if native removal fails.
            Retire();
        }
    }

    private void Retire()
    {
        Action? finished;
        lock (_sync)
        {
            Handle = IntPtr.Zero; _callback = null;
            _ownership?.Dispose(); _ownership = null;
            if (_reference != null) { FunctionReference.Remove(_reference.Identifier); _reference = null; }
            lock (ActiveSync) Active.Remove(this);
            finished = Finished; Finished = null;
        }
        finished?.Invoke();
    }

    internal static void StopMapTimers()
    {
        TimerState[] timers;
        lock (ActiveSync) timers = Active.Where(t => (t._flags & TimerFlags.STOP_ON_MAPCHANGE) != 0).ToArray();
        foreach (var timer in timers) timer.Stop();
    }
}
