using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Plugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CounterStrikeSharp.API.Tests;

public class CapabilityOwnershipTests
{
    private static string Name() => "test:" + Guid.NewGuid();

    [Fact]
    public void ReloadReturnsNewSupplierAndPreservesOtherProviders()
    {
        var capability = new PluginCapability<string>(Name());
        var old = new AssemblyLoadContext(Name(), true);
        var other = new AssemblyLoadContext(Name(), true);
        var next = new AssemblyLoadContext(Name(), true);
        using (old.EnterContextualReflection()) Capabilities.RegisterPluginCapability(capability, () => "old");
        using (other.EnterContextualReflection()) Capabilities.RegisterPluginCapability(capability, () => "other");
        Assert.Equal("old", capability.Get());
        CapabilityOwnership.Release(old); // manual unload, before ALC disposal
        Assert.Equal("other", capability.Get());
        using (next.EnterContextualReflection()) Capabilities.RegisterPluginCapability(capability, () => "new");
        other.Unload();
        Assert.Equal("new", capability.Get());
        old.Unload(); // repeated cleanup cannot remove the new registration
        Assert.Equal("new", capability.Get());
        next.Unload();
        Assert.Null(capability.Get());
    }

    [Fact]
    public void EqualPlayerSuppliersStillHaveIndependentOwnership()
    {
        var capability = new PlayerCapability<CCSPlayerController>(Name());
        Func<CCSPlayerController, CCSPlayerController> supplier = p => p;
        var first = new AssemblyLoadContext(Name(), true);
        var second = new AssemblyLoadContext(Name(), true);
        using (first.EnterContextualReflection()) Capabilities.RegisterPlayerCapability(capability, supplier);
        using (second.EnterContextualReflection()) Capabilities.RegisterPlayerCapability(capability, supplier);
        first.Unload();
        var player = new CCSPlayerController(IntPtr.Zero);
        Assert.Same(player, capability.Get(player));
        second.Unload();
        Assert.Null(capability.Get(player));
    }

    [Fact]
    public void LateRegistrationAfterUnloadCannotResurrectProvider()
    {
        var context = new AssemblyLoadContext(Name(), true);
        var capability = new PluginCapability<string>(Name());
        using (context.EnterContextualReflection())
        {
            Capabilities.RegisterPluginCapability(capability, () => "old");
            CapabilityOwnership.Release(context);
            Assert.Throws<InvalidOperationException>(() => Capabilities.RegisterPluginCapability(capability, () => "late"));
            Assert.Null(capability.Get());
        }
        context.Unload();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PluginUnloadReleasesCapabilitiesEvenWhenPluginCleanupThrows(bool unloadThrows, bool disposeThrows)
    {
        var owner = new AssemblyLoadContext(Name(), true);
        var capability = new PluginCapability<string>(Name());
        using (owner.EnterContextualReflection()) Capabilities.RegisterPluginCapability(capability, () => "old");
        var plugin = new CleanupPlugin(unloadThrows, disposeThrows);
        var scope = new Mock<IServiceScope>();
        // Exercise the real Unload method without creating a native host or loader.
        var context = (PluginContext)RuntimeHelpers.GetUninitializedObject(typeof(PluginContext));
        void Set(string field, object value) => typeof(PluginContext).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(context, value);
        Set("<Plugin>k__BackingField", plugin);
        Set("_capabilityContext", owner);
        Set("_serviceScope", scope.Object);
        Set("_logger", NullLogger<PluginContext>.Instance);
        context.State = PluginState.Loaded;
        if (disposeThrows) Assert.Throws<InvalidOperationException>(() => context.Unload(true));
        else context.Unload(true);
        Assert.Null(capability.Get());
        scope.Verify(s => s.Dispose(), Times.Once);
        owner.Unload();
    }

    [Fact]
    public void SupplierAssemblyIdentifiesOwnerOutsideLoadScopeAndDoesNotPinIt()
    {
        var weak = RegisterAndUnloadCollectibleSupplier();
        for (var i = 0; weak.IsAlive && i < 20; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndUnloadCollectibleSupplier()
    {
        var context = new AssemblyLoadContext(Name(), true);
        var assembly = context.LoadFromAssemblyPath(typeof(CapabilitySupplierFixture).Assembly.Location);
        var supplier = (Func<string>)assembly.GetType(typeof(CapabilitySupplierFixture).FullName!)!
            .GetMethod(nameof(CapabilitySupplierFixture.Create))!.Invoke(null, null)!;
        var capability = new PluginCapability<string>(Name());
        Capabilities.RegisterPluginCapability(capability, supplier);
        Assert.Equal("collectible", capability.Get());
        context.Unload();
        Assert.Null(capability.Get());
        return new WeakReference(context);
    }

    private sealed class CleanupPlugin(bool unloadThrows, bool disposeThrows) : IPlugin
    {
        public string ModuleName => "test";
        public string ModuleVersion => "1";
        public string ModuleAuthor => "test";
        public string ModuleDescription => "test";
        public string ModulePath { get; set; } = "";
        public Microsoft.Extensions.Logging.ILogger Logger { get; set; } = null!;
        public Microsoft.Extensions.Localization.IStringLocalizer Localizer { get; set; } = null!;
        public CounterStrikeSharp.API.Core.Commands.ICommandManager CommandManager { get; set; } = null!;
        public void Load(bool hotReload) { }
        public void Unload(bool hotReload) { if (unloadThrows) throw new InvalidOperationException(); }
        public void Dispose() { if (disposeThrows) throw new InvalidOperationException(); }
        public void OnAllPluginsLoaded(bool hotReload) { }
        public void RegisterAllAttributes(object instance) { }
        public void InitializeConfig(object instance, Type pluginType) { }
    }
}

public static class CapabilitySupplierFixture
{
    public static Func<string> Create() => () => "collectible";
}
