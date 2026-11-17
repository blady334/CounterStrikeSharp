using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace CounterStrikeSharp.API.Core;

internal enum WorkLane { Frame, World, Tick }

internal sealed class DeferredWorkScheduler
{
    private sealed class Work
    {
        internal Action? Run, Cancel;
        internal Delegate? Source;
        internal WorkOwnership.Registration? Ownership;
        internal LinkedListNode<Work>? Node;
        internal WorkLane Lane;
        internal int Tick;
    }
    private readonly object _sync = new();
    private readonly LinkedList<Work> _frame = new(), _world = new();
    private readonly SortedDictionary<int, LinkedList<Work>> _ticks = new();
    private int _tickCount;
    // Includes detached batches until execution starts, so unloading/map changes
    // can still cancel the second item while the first item is executing.
    private readonly HashSet<Work> _pending = new();
    internal bool HasTickWork { get { lock (_sync) return _ticks.Count != 0; } }

    internal Task<T> Enqueue<T>(Func<T> callback, Delegate source, WorkLane lane, int tick = 0, bool logFailure = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Preserve the existing successful-await behavior: continuations may
        // run inline on the game thread. Complete only outside scheduler locks.
        var completion = new TaskCompletionSource<T>();
        var work = new Work { Source = source, Lane = lane, Tick = tick };
        work.Run = () => {
            try { completion.TrySetResult(callback()); }
            catch (Exception error)
            {
                completion.TrySetException(error);
                if (logFailure)
                {
                    _ = completion.Task.Exception;
                    Application.Instance.Logger.LogError(error, "Deferred {Lane} task failed: {Assembly}.{Method}",
                        lane, source.Method.Module.Assembly.GetName().Name, source.Method.Name);
                }
            }
        };
        work.Cancel = () => completion.TrySetCanceled();
        lock (_sync)
        {
            work.Ownership = new WorkOwnership.Registration(source, () => Cancel(work));
            if (!work.Ownership.Rejected)
            {
                LinkedList<Work> queue;
                if (lane == WorkLane.Tick)
                {
                    if (!_ticks.TryGetValue(tick, out queue!)) _ticks.Add(tick, queue = new());
                    _tickCount++;
                }
                else queue = lane == WorkLane.Frame ? _frame : _world;
                work.Node = queue.AddLast(work);
                _pending.Add(work);
            }
        }
        if (work.Ownership?.Rejected == true) Cancel(work);
        return completion.Task;
    }

    private void RemoveNode(Work work)
    {
        if (work.Node == null) return;
        var queue = work.Node.List!;
        queue.Remove(work.Node);
        work.Node = null;
        if (work.Lane == WorkLane.Tick)
        {
            _tickCount--;
            if (queue.Count == 0) _ticks.Remove(work.Tick);
        }
    }

    private void Cancel(Work work)
    {
        Action? cancel;
        lock (_sync)
        {
            RemoveNode(work);
            _pending.Remove(work);
            cancel = work.Cancel;
            work.Run = null; work.Cancel = null; work.Source = null;
            work.Ownership?.Dispose(); work.Ownership = null;
        }
        cancel?.Invoke();
    }

    internal void CancelMapWork()
    {
        Work[] pending;
        lock (_sync) pending = _pending.ToArray();
        foreach (var work in pending) Cancel(work);
    }
    internal void CancelTickWork()
    {
        Work[] pending;
        lock (_sync) pending = _pending.Where(w => w.Lane == WorkLane.Tick).ToArray();
        foreach (var work in pending) Cancel(work);
    }

    internal (int Frame, int World, int Tick) Counts
    {
        get { lock (_sync) return (_frame.Count, _world.Count, _tickCount); }
    }

    internal void Drain(WorkLane lane, int limit, int currentTick = 0)
    {
        List<Work> batch;
        lock (_sync)
        {
            if (lane == WorkLane.Frame && _frame.Count == 0 || lane == WorkLane.World && _world.Count == 0 ||
                lane == WorkLane.Tick && (_ticks.Count == 0 || _ticks.First().Key > currentTick)) return;
            batch = new List<Work>();
            limit = Math.Max(1, limit);
            while (batch.Count < limit)
            {
                LinkedList<Work>? queue;
                if (lane == WorkLane.Tick)
                {
                    if (_ticks.Count == 0 || _ticks.First().Key > currentTick) break;
                    queue = _ticks.First().Value;
                }
                else queue = lane == WorkLane.Frame ? _frame : _world;
                if (queue.First == null) break;
                var work = queue.First.Value;
                RemoveNode(work);
                batch.Add(work);
            }
        }
        foreach (var work in batch)
        {
            Action? run;
            Delegate? source;
            lock (_sync)
            {
                run = work.Run; source = work.Source;
                work.Run = null; work.Cancel = null; work.Source = null;
                _pending.Remove(work);
                work.Ownership?.Dispose(); work.Ownership = null;
            }
            if (run == null) continue;
            var started = RuntimeDiagnostics.Begin();
            try { run(); }
            finally { if (started != 0) RuntimeDiagnostics.End(source!, started, lane.ToString()); }
        }
    }
}
