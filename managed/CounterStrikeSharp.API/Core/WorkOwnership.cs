using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace CounterStrikeSharp.API.Core;

// A generation can close before its ALC is collected (manual unload/reload).
// Completed work unregisters immediately, so this registry cannot accumulate it.
internal static class WorkOwnership
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, Owner> Owners = new();
    private sealed class Owner
    {
        internal bool Closed;
        internal readonly HashSet<Registration> Pending = new();
    }

    internal sealed class Registration : IDisposable
    {
        private Owner[] _owners = Array.Empty<Owner>();
        private Action? _cancel;
        internal bool Rejected { get; private set; }
        internal Registration(Delegate callback, Action cancel)
        {
            var contexts = new HashSet<AssemblyLoadContext>();
            foreach (var part in callback.GetInvocationList())
            {
                var context = AssemblyLoadContext.GetLoadContext(part.Method.Module.Assembly);
                if (context?.IsCollectible != true && part.Target != null)
                    context = AssemblyLoadContext.GetLoadContext(part.Target.GetType().Assembly);
                if (context?.IsCollectible == true) contexts.Add(context);
            }
            if (contexts.Count == 0 && AssemblyLoadContext.CurrentContextualReflectionContext is { IsCollectible: true } ambient)
                contexts.Add(ambient);
            if (contexts.Count == 0) return;
            lock (Sync)
            {
                _owners = contexts.Select(context => Owners.GetValue(context, c => { c.Unloading += Release; return new Owner(); })).ToArray();
                if (_owners.Any(o => o.Closed)) { Rejected = true; _owners = Array.Empty<Owner>(); return; }
                _cancel = cancel;
                foreach (var owner in _owners) owner.Pending.Add(this);
            }
        }
        public void Dispose()
        {
            lock (Sync) { Detach(); }
        }
        internal Action? Detach()
        {
            var cancel = _cancel;
            foreach (var owner in _owners) owner.Pending.Remove(this);
            _cancel = null; _owners = Array.Empty<Owner>();
            return cancel;
        }
    }

    internal static void Release(AssemblyLoadContext? context)
    {
        if (context == null) return;
        Action[] cancellations;
        lock (Sync)
        {
            var owner = Owners.GetValue(context, _ => new Owner());
            if (owner.Closed) return;
            owner.Closed = true;
            cancellations = owner.Pending.ToArray().Select(r => r.Detach()).OfType<Action>().ToArray();
            owner.Pending.Clear();
        }
        // Never run callbacks or native cleanup while holding the registry lock.
        foreach (var cancel in cancellations) cancel();
    }
}
