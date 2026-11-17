using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace CounterStrikeSharp.API.Core.Capabilities;

// Registrations belong to a loaded generation, not a plugin name or capability type.
internal static class CapabilityOwnership
{
    internal static readonly object Sync = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, Owner> Owners = new();

    private sealed class Owner
    {
        internal bool Closed;
        internal readonly List<Action> Removals = new();
    }

    private static AssemblyLoadContext? FindOwner(Delegate supplier)
    {
        var context = AssemblyLoadContext.GetLoadContext(supplier.Method.Module.Assembly);
        if (context?.IsCollectible == true) return context;
        if (supplier.Target != null)
        {
            context = AssemblyLoadContext.GetLoadContext(supplier.Target.GetType().Assembly);
            if (context?.IsCollectible == true) return context;
        }
        context = AssemblyLoadContext.CurrentContextualReflectionContext;
        return context?.IsCollectible == true ? context : null;
    }

    // Called with Sync held, so registration and cleanup cannot race.
    internal static void Register(Delegate supplier, Action add, Action remove)
    {
        var context = FindOwner(supplier);
        Owner? owner = null;
        if (context != null)
        {
            owner = Owners.GetValue(context, c =>
            {
                c.Unloading += Release;
                return new Owner();
            });
            if (owner.Closed)
                throw new InvalidOperationException("Cannot register a capability from an unloaded plugin.");
        }
        add();
        owner?.Removals.Add(remove);
    }

    internal static void Release(AssemblyLoadContext? context)
    {
        if (context == null) return;
        lock (Sync)
        {
            // Keep a closed tombstone until the context is collected; late callbacks
            // must not resurrect suppliers after manual unload.
            var owner = Owners.GetValue(context, _ => new Owner());
            if (owner.Closed) return;
            owner.Closed = true;
            foreach (var remove in owner.Removals) remove();
            owner.Removals.Clear();
        }
    }
}
