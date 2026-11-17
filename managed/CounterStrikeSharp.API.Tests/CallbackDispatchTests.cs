using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;
using Microsoft.Extensions.Logging;
using Moq;

namespace CounterStrikeSharp.API.Tests;

[Collection("Callback ownership")]
public unsafe class CallbackDispatchTests
{
    private readonly Mock<ILoggerFactory> _factory = new();
    private readonly Mock<ILogger> _logger = new();

    public CallbackDispatchTests()
    {
        _factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);
        _ = new Application(_factory.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static long Invoke(Delegate handler, FunctionLifetime lifetime = FunctionLifetime.Permanent, params long[] args)
    {
        var reference = FunctionReference.Create(handler, lifetime);
        try
        {
            fxScriptContext context = default;
            context.numArguments = args.Length;
            for (int i = 0; i < args.Length; i++) ((long*)context.functionData)[i] = args[i];
            // Exercise the reverse P/Invoke entry used by native callbacks.
            ((delegate* unmanaged<fxScriptContext*, void>)reference.GetFunctionPointer())(&context);
            Assert.True(reference.CompletionTask.IsCompleted);
            return *(long*)context.result;
        }
        finally { FunctionReference.Remove(reference.Identifier); }
    }

    [Fact]
    public void PrimitiveArgumentsAndHookResultRoundTrip()
    {
        Assert.Equal((long)HookResult.Handled, Invoke(new Func<int, IntPtr, HookResult>((number, ptr) =>
        {
            Assert.Equal(42, number); Assert.Equal(new IntPtr(1234), ptr);
            return HookResult.Handled;
        }), FunctionLifetime.Permanent, 42, 1234));
    }

    [Fact]
    public void ScriptContextHandlerReadsOriginalArguments()
    {
        Assert.Equal(87, Invoke(new Func<ScriptContext, int>(c => c.GetArgument<int>(0) + 1), FunctionLifetime.Permanent, 86));
    }

    [Fact]
    public void MulticastDelegateCallsEveryTargetInOrderAndReturnsLastResult()
    {
        var calls = new List<int>();
        Func<int> handler = () => { calls.Add(1); return 10; };
        handler += () => { calls.Add(2); return 20; };
        Assert.Equal(20, Invoke(handler));
        Assert.Equal(new[] { 1, 2 }, calls);
    }

    private static int BoundStatic(string prefix, int value) => prefix.Length + value;

    [Fact]
    public void ClosedStaticDelegateUsesInvokeSignatureNotUnderlyingMethodParameters()
    {
        var method = typeof(CallbackDispatchTests).GetMethod(nameof(BoundStatic), BindingFlags.NonPublic | BindingFlags.Static)!;
        var handler = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), "abc", method);
        Assert.Equal(45, Invoke(handler, FunctionLifetime.Permanent, 42));
    }

    [Fact]
    public void OpenInstanceDelegateReceivesItsReceiverArgument()
    {
        // ScriptContext itself can be supplied as the open-instance receiver.
        var method = typeof(ScriptContext).GetMethod(nameof(ScriptContext.GetResultPrimitive))!.MakeGenericMethod(typeof(int));
        var handler = (Func<ScriptContext, int>)method.CreateDelegate(typeof(Func<ScriptContext, int>));
        Assert.Equal(0, Invoke(handler));
        _logger.Verify(l => l.Log(It.Is<LogLevel>(level => level == LogLevel.Error), It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleUseCompletionAndCleanupRunOnExceptions(bool termination)
    {
        Action handler = () => throw (termination ? new PluginTerminationException("test") : new InvalidOperationException("test"));
        var reference = FunctionReference.Create(handler, FunctionLifetime.SingleUse);
        fxScriptContext context = default;
        ((delegate* unmanaged<fxScriptContext*, void>)reference.GetFunctionPointer())(&context);
        Assert.True(reference.CompletionTask.IsCompletedSuccessfully);
        var entries = (System.Collections.IDictionary)typeof(FunctionReference)
            .GetField("IdToFunctionReferencesMap", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.False(entries.Contains(reference.Identifier));
        _logger.Verify(l => l.Log(It.Is<LogLevel>(level => level == LogLevel.Error), It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            termination ? Times.Never() : Times.Once());
    }

    [Fact]
    public void StringBuffersAreStillCleanedUpAndContextCanBeReused()
    {
        var context = new ScriptContext();
        fxScriptContext raw = default;
        context.Push(&raw, "zażółć");
        Assert.Equal("zażółć", Marshal.PtrToStringUTF8(*(IntPtr*)raw.functionData));
        context.GlobalCleanUp();
        context.GlobalCleanUp();
        raw = default;
        context.Push(&raw, "again");
        Assert.Equal("again", Marshal.PtrToStringUTF8(*(IntPtr*)raw.functionData));
        context.GlobalCleanUp();
    }

    [Fact]
    public void InvokerCacheDoesNotKeepCollectibleDelegateTypesAlive()
    {
        var weak = CreateCollectibleReference();
        for (int i = 0; i < 20 && weak.IsAlive; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCollectibleReference()
    {
        var context = new AssemblyLoadContext("callback-test", true);
        var assembly = context.LoadFromAssemblyPath(typeof(CallbackDispatchFixture).Assembly.Location);
        var handler = (Delegate)assembly.GetType(typeof(CallbackDispatchFixture).FullName!)!
            .GetMethod(nameof(CallbackDispatchFixture.Create))!.Invoke(null, null)!;
        var reference = FunctionReference.Create(handler);
        FunctionReference.Remove(reference.Identifier);
        context.Unload();
        return new WeakReference(context);
    }
}

public static class CallbackDispatchFixture
{
    public delegate int Handler();
    public static Handler Create() => () => 7;
}
