using System.Reflection;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using Moq;

namespace CounterStrikeSharp.API.Tests;

[Collection("Callback ownership")]
public class RuntimeDiagnosticsTests
{
    private static void DiagnosticTarget() { }
    [Fact]
    public unsafe void WrappedHandlerIsReportedAsTheOriginalPluginMethod()
    {
        _=new Application(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            null!,null!,null!,null!,null!,null!,null!,null!);
        var field=typeof(CoreConfig).GetField("_coreConfig",BindingFlags.Static|BindingFlags.NonPublic)!;
        var original=field.GetValue(null);
        var reference=FunctionReference.CreateUnique((Action)(()=>Thread.Sleep(5)),(Action)DiagnosticTarget);
        try
        {
            field.SetValue(null,new CoreConfigData { RuntimeDiagnosticsEnabled=true,SlowCallbackMilliseconds=0.1 });
            RuntimeDiagnostics.Reset();
            fxScriptContext context=default;
            ((delegate* unmanaged<fxScriptContext*,void>)reference.GetFunctionPointer())(&context);
            var sample=Assert.Single(RuntimeDiagnostics.TakeSamples());
            Assert.EndsWith("."+nameof(DiagnosticTarget),sample.Name);
        }
        finally { FunctionReference.Remove(reference.Identifier); field.SetValue(null,original); RuntimeDiagnostics.Reset(); }
    }
    [Fact]
    public void DisabledByDefaultAndBoundedWhenEnabled()
    {
        Assert.False(new CoreConfigData().RuntimeDiagnosticsEnabled);
        Assert.Equal(0,RuntimeDiagnostics.Begin());
        RuntimeDiagnostics.Reset();
        for(int i=0;i<500;i++) RuntimeDiagnostics.Record("method"+i,10);
        Assert.Equal(128,RuntimeDiagnostics.TakeSamples().Length);
        Assert.Empty(RuntimeDiagnostics.TakeSamples());
        RuntimeDiagnostics.Reset();
    }

    [Fact]
    public void SummaryIsRateLimitedAndDoesNotLogEveryCallback()
    {
        var field=typeof(CoreConfig).GetField("_coreConfig",BindingFlags.Static|BindingFlags.NonPublic)!;
        var original=field.GetValue(null);
        var logger=new Mock<ILogger>(); var factory=new Mock<ILoggerFactory>();
        factory.Setup(f=>f.CreateLogger(It.IsAny<string>())).Returns(logger.Object);
        _=new Application(factory.Object,null!,null!,null!,null!,null!,null!,null!,null!);
        try
        {
            field.SetValue(null,new CoreConfigData { RuntimeDiagnosticsEnabled=true,DiagnosticsIntervalSeconds=30 });
            RuntimeDiagnostics.Reset();
            typeof(RuntimeDiagnostics).GetField("_lastReport",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,0L);
            for(int i=0;i<100;i++) RuntimeDiagnostics.Record("plugin.callback",12);
            Assert.Empty(logger.Invocations.Where(i=>i.Method.Name=="Log"));
            RuntimeDiagnostics.Report((300,0,0));
            RuntimeDiagnostics.Record("plugin.callback",20);
            RuntimeDiagnostics.Report((300,0,0));
            Assert.Single(logger.Invocations.Where(i=>i.Method.Name=="Log"));
        }
        finally { field.SetValue(null,original); RuntimeDiagnostics.Reset(); }
    }
}
