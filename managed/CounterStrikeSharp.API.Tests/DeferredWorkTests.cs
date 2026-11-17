using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Reflection;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CounterStrikeSharp.API.Tests;

[Collection("Callback ownership")]
public class DeferredWorkTests
{
    public DeferredWorkTests() => Server.Work.CancelMapWork();
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnloadedPluginWorkIsCanceledAndOtherPluginStillRuns(bool world)
    {
        _ = new Application(NullLoggerFactory.Instance, null!, null!, null!, null!, null!, null!, null!, null!);
        var owner = new AssemblyLoadContext("old", true);
        var other = new AssemblyLoadContext("other", true);
        int oldCalls = 0, otherCalls = 0;
        Task old, next;
        using (owner.EnterContextualReflection()) old = world ? Server.NextWorldUpdateAsync(() => oldCalls++) : Server.NextFrameAsync(() => oldCalls++);
        using (other.EnterContextualReflection()) next = world ? Server.NextWorldUpdateAsync(() => otherCalls++) : Server.NextFrameAsync(() => otherCalls++);
        owner.Unload();
        Assert.True(old.IsCanceled);
        if (world) Server.OnWorldUpdate(); else Server.OnTick();
        Assert.Equal(0, oldCalls);
        Assert.Equal(1, otherCalls);
        Assert.True(next.IsCompletedSuccessfully);
        other.Unload();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateWorkFromUnloadedGenerationIsCanceled(bool world)
    {
        var owner = new AssemblyLoadContext("closed", true);
        using (owner.EnterContextualReflection())
        {
            var first = world ? Server.NextWorldUpdateAsync(() => 1) : Server.NextFrameAsync(() => 1);
            owner.Unload();
            var late = world ? Server.NextWorldUpdateAsync(() => 2) : Server.NextFrameAsync(() => 2);
            Assert.True(first.IsCanceled);
            Assert.True(late.IsCanceled);
        }
    }

    [Fact]
    public void BudgetAndNewlyQueuedTasksRespectTheNextPass()
    {
        var scheduler = new DeferredWorkScheduler();
        var calls = new List<int>();
        Func<int> second = () => { calls.Add(2); return 2; };
        Func<int> first = () => { calls.Add(1); scheduler.Enqueue(second, second, WorkLane.Frame); return 1; };
        scheduler.Enqueue(first, first, WorkLane.Frame);
        scheduler.Drain(WorkLane.Frame, 100);
        Assert.Equal(new[]{1}, calls);
        scheduler.Enqueue(second, second, WorkLane.Frame);
        scheduler.Drain(WorkLane.Frame, 1);
        Assert.Equal(1, scheduler.Counts.Frame);
        scheduler.Drain(WorkLane.Frame, 1);
        Assert.Equal(new[]{1,2,2}, calls);
    }

    [Fact]
    public void TickTasksAreOrderedBoundedAndPropagateErrors()
    {
        var calls = new List<int>();
        var future = Server.RunOnTickAsync(20, () => calls.Add(20));
        var first = Server.RunOnTickAsync(10, () => calls.Add(1));
        var second = Server.RunOnTickAsync(10, () => calls.Add(2));
        var failed = Server.RunOnTickAsync(10, () => throw new InvalidOperationException("test"));
        Server.Work.Drain(WorkLane.Tick, 1, 9); Assert.Empty(calls);
        Server.Work.Drain(WorkLane.Tick, 1, 10); Assert.Equal(new[]{1}, calls);
        Server.Work.Drain(WorkLane.Tick, 10, 10); Assert.Equal(new[]{1,2}, calls);
        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully);
        Assert.IsType<InvalidOperationException>(failed.Exception!.InnerException);
        Assert.False(future.IsCompleted);
        Server.OnMapEnd(); Assert.True(future.IsCanceled);
        Assert.Equal((0,0,0), Server.Work.Counts);
    }

    [Fact]
    public void MapEndCancelsAllPendingLanesAndWorkshopRestartOnlyOldTicks()
    {
        Server.OnMapStart();
        var frame = Server.NextFrameAsync(() => 1);
        var world = Server.NextWorldUpdateAsync(() => 2);
        var tick = Server.RunOnTickAsync(100, () => { });
        Server.OnMapStart();
        Assert.True(tick.IsCanceled);
        Assert.False(frame.IsCompleted || world.IsCompleted);
        Server.OnMapEnd();
        Assert.True(frame.IsCanceled && world.IsCanceled);
    }

    [Fact]
    public void UnloadOrMapEndCancelsItemsAlreadyDetachedForDispatch()
    {
        var scheduler = new DeferredWorkScheduler();
        var owner = new AssemblyLoadContext("batch", true);
        Func<int> first = () => { WorkOwnership.Release(owner); return 1; };
        scheduler.Enqueue(first, first, WorkLane.Frame);
        Task<int> canceled;
        using (owner.EnterContextualReflection()) canceled = scheduler.Enqueue(() => 2, (Func<int>)(() => 2), WorkLane.Frame);
        scheduler.Drain(WorkLane.Frame, 10);
        Assert.True(canceled.IsCanceled);
        Func<int> changeMap = () => { scheduler.CancelMapWork(); return 0; };
        scheduler.Enqueue(changeMap, changeMap, WorkLane.Frame);
        var mapCanceled = scheduler.Enqueue(() => 3, (Func<int>)(() => 3), WorkLane.Frame);
        scheduler.Drain(WorkLane.Frame, 10);
        Assert.True(mapCanceled.IsCanceled);
        owner.Unload();
    }

    [Fact]
    public void WorldWorkRunsWithoutDrainingFrameOrTickTasks()
    {
        var frame = Server.NextFrameAsync(() => 1);
        var tick = Server.RunOnTickAsync(0, () => { });
        var world = Server.NextWorldUpdateAsync(() => 3);
        Server.OnWorldUpdate();
        Assert.Equal(3, world.Result);
        Assert.False(frame.IsCompleted || tick.IsCompleted);
        Server.OnMapEnd();
    }

    [Fact]
    public void SuccessfulContinuationsPreserveGameThreadBehavior()
    {
        int gameThread = Environment.CurrentManagedThreadId;
        var task = Server.NextFrameAsync(() => 1);
        var continuation = task.ContinueWith(_ => Environment.CurrentManagedThreadId,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Server.OnTick();
        Assert.Equal(gameThread, continuation.GetAwaiter().GetResult());
    }

    [Fact]
    public void CancellationCompletesOutsideTheSchedulerLock()
    {
        var task=Server.NextFrameAsync(()=>1);
        var continuation=task.ContinueWith(_ => {
            var worker=Task.Run(()=>Server.NextFrame(()=>{}));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(2)));
        },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        Server.OnMapEnd();
        continuation.GetAwaiter().GetResult();
        Server.OnMapEnd();
    }

    [Fact]
    public void RepeatedReloadReleasesCollectibleCallbacksAndLeavesNoQueuedWork()
    {
        var contexts = Enumerable.Range(0, 20).Select(_ => EnqueueAndUnload()).ToArray();
        for (int attempt=0; attempt<20 && contexts.Any(w => w.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(contexts, weak => Assert.False(weak.IsAlive));
        Assert.Equal((0,0,0), Server.Work.Counts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EnqueueAndUnload()
    {
        var owner = new AssemblyLoadContext("reload", true);
        var assembly = owner.LoadFromAssemblyPath(typeof(CapabilitySupplierFixture).Assembly.Location);
        var supplier = (Func<string>)assembly.GetType(typeof(CapabilitySupplierFixture).FullName!)!
            .GetMethod(nameof(CapabilitySupplierFixture.Create))!.Invoke(null,null)!;
        var frame = Server.NextFrameAsync(supplier);
        var world = Server.NextWorldUpdateAsync(supplier);
        owner.Unload();
        Assert.True(frame.IsCanceled && world.IsCanceled);
        return new WeakReference(owner);
    }

    [Fact]
    public async Task ConcurrentProducersAndUnloadLeaveNoOldGenerationWork()
    {
        var scheduler = new DeferredWorkScheduler();
        var owner = new AssemblyLoadContext("workers", true);
        var tasks = new System.Collections.Concurrent.ConcurrentBag<Task<int>>();
        await Task.WhenAll(Enumerable.Range(0,4).Select(_ => Task.Run(() => {
            using (owner.EnterContextualReflection())
                for(int i=0;i<200;i++) tasks.Add(scheduler.Enqueue(() => 1, (Func<int>)(() => 1), WorkLane.Tick, 999));
        })));
        WorkOwnership.Release(owner);
        Assert.All(tasks, t => Assert.True(t.IsCanceled));
        Assert.Equal((0,0,0), scheduler.Counts);
        owner.Unload();
    }

    [Fact]
    public void MulticastTaskIsCanceledWhenAnyContributingPluginUnloads()
    {
        var first=new AssemblyLoadContext("first",true);
        var second=new AssemblyLoadContext("second",true);
        Action Create(AssemblyLoadContext owner) => (Action)owner.LoadFromAssemblyPath(typeof(WorkFixture).Assembly.Location)
            .GetType(typeof(WorkFixture).FullName!)!.GetMethod(nameof(WorkFixture.Create))!.Invoke(null,null)!;
        var task=Server.NextFrameAsync(Create(first)+Create(second));
        first.Unload(); Assert.True(task.IsCanceled);
        second.Unload(); Assert.Equal((0,0,0),Server.Work.Counts);
    }
}

public static class WorkFixture
{
    public static Action Create() => () => { };
}
