using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging.Abstractions;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CounterStrikeSharp.API.Tests;

[Collection("Callback ownership")]
public unsafe class TimerLifetimeTests
{
    public TimerLifetimeTests() => _ = new Application(NullLoggerFactory.Instance, null!, null!, null!, null!, null!, null!, null!, null!);
    private static IDictionary References => (IDictionary)typeof(FunctionReference)
        .GetField("IdToFunctionReferencesMap", BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;

    private sealed class Backend : ITimerBackend
    {
        internal readonly Dictionary<IntPtr,(IntPtr Pointer,int Id,int Flags)> Callbacks = new();
        internal int Kills;
        internal bool FailCreate;
        private int _next;
        public IntPtr Create(float interval, FunctionReference reference, int flags)
        {
            if (FailCreate) throw new InvalidOperationException("create failed");
            var handle = (IntPtr)(++_next);
            Callbacks.Add(handle,(reference.GetFunctionPointer(),reference.Identifier,flags));
            return handle;
        }
        public void Kill(IntPtr handle) { Assert.True(Callbacks.Remove(handle)); Kills++; }
        internal void Fire(IntPtr handle)
        {
            var callback = Callbacks[handle];
            fxScriptContext context=default;
            ((delegate* unmanaged<fxScriptContext*,void>)callback.Pointer)(&context);
            if ((callback.Flags & (int)TimerFlags.REPEAT)==0) Callbacks.Remove(handle);
        }
    }

    [Fact]
    public void EqualCallbacksHaveIndependentReferencesAndOneShotCompletionReleasesThem()
    {
        var backend=new Backend(); int calls=0;
        Action action=()=>calls++;
        var first=new Timer(0,action,null,backend);
        var second=new Timer(0,action,null,backend);
        var firstId=backend.Callbacks[first.Handle].Id;
        var secondId=backend.Callbacks[second.Handle].Id;
        Assert.NotEqual(firstId,secondId);
        backend.Fire(first.Handle);
        Assert.False(References.Contains(firstId)); Assert.True(References.Contains(secondId));
        Assert.Equal(IntPtr.Zero,first.Handle);
        first.Kill(); Assert.Equal(0,backend.Kills);
        backend.Fire(second.Handle); Assert.Equal(2,calls);
        Assert.False(References.Contains(secondId));
    }

    [Fact]
    public void KillDuringCallbackAndRepeatedKillAreSafe()
    {
        var backend=new Backend(); Timer? timer=null;
        timer=new Timer(0,()=>timer!.Kill(),TimerFlags.REPEAT,backend);
        int id=backend.Callbacks[timer.Handle].Id;
        backend.Fire(timer.Handle); timer.Kill();
        Assert.Equal(1,backend.Kills); Assert.False(References.Contains(id));
    }

    [Fact]
    public void OwnerUnloadMakesNativeTrampolineInertUntilGameThreadCleanup()
    {
        var backend=new Backend(); var owner=new AssemblyLoadContext("timer",true);
        int calls=0; Timer timer;
        using(owner.EnterContextualReflection()) timer=new Timer(0,()=>calls++,TimerFlags.REPEAT,backend);
        int id=backend.Callbacks[timer.Handle].Id;
        owner.Unload();
        backend.Fire(timer.Handle); Assert.Equal(0,calls);
        Assert.True(References.Contains(id)); // Native code still holds the pointer.
        Server.OnWorldUpdate();
        Assert.False(References.Contains(id)); Assert.Equal(1,backend.Kills);
        Assert.Equal(IntPtr.Zero,timer.Handle);
    }

    [Fact]
    public void MapChangeStopsOnlyFlaggedTimersAndDoesNotLeaveStaleHandles()
    {
        var backend=new Backend();
        var stop=new Timer(0,()=>{},TimerFlags.REPEAT|TimerFlags.STOP_ON_MAPCHANGE,backend);
        var persist=new Timer(0,()=>{},TimerFlags.REPEAT,backend);
        Server.OnMapEnd(); Assert.Equal(IntPtr.Zero,stop.Handle);
        Assert.NotEqual(IntPtr.Zero,persist.Handle);
        stop.Kill(); Assert.Equal(1,backend.Kills);
        persist.Kill(); Assert.Equal(2,backend.Kills);
    }

    [Fact]
    public void FailedCreationDoesNotLeakFunctionReferences()
    {
        int before=References.Count;
        Assert.Throws<InvalidOperationException>(()=>new Timer(0,()=>{},null,new Backend { FailCreate=true }));
        Assert.Equal(before,References.Count);
    }

    [Fact]
    public void RepeatedOwnerReloadDoesNotAccumulateTimerReferences()
    {
        int before=References.Count;
        for(int i=0;i<100;i++)
        {
            var backend=new Backend(); var owner=new AssemblyLoadContext("timer-reload",true);
            using(owner.EnterContextualReflection()) _=new Timer(0,()=>{},TimerFlags.REPEAT,backend);
            WorkOwnership.Release(owner); Server.OnWorldUpdate(); owner.Unload();
            Assert.Empty(backend.Callbacks);
        }
        Assert.Equal(before,References.Count);
    }

    [Fact]
    public void UnloadedTimerDoesNotKeepItsPluginAssemblyAlive()
    {
        var weak=CreateAndUnloadTimer();
        for(int i=0;i<20 && weak.IsAlive;i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndUnloadTimer()
    {
        var owner=new AssemblyLoadContext("timer-gc",true);
        var callback=(Action)owner.LoadFromAssemblyPath(typeof(WorkFixture).Assembly.Location)
            .GetType(typeof(WorkFixture).FullName!)!.GetMethod(nameof(WorkFixture.Create))!.Invoke(null,null)!;
        _=new Timer(0,callback,TimerFlags.REPEAT,new Backend());
        owner.Unload(); Server.OnWorldUpdate();
        return new WeakReference(owner);
    }
}
