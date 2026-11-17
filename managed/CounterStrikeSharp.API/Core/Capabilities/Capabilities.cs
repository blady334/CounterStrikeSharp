namespace CounterStrikeSharp.API.Core.Capabilities;

public static class Capabilities
{
    public static void RegisterPluginCapability<T>(PluginCapability<T> capability, Func<T> supplier)
    {
        lock (CapabilityOwnership.Sync)
        {
            if (!PluginCapability<T>.Providers.TryGetValue(capability.Name, out var providers))
                PluginCapability<T>.Providers.Add(capability.Name, providers = new());
            // Each registration has its own identity, even for the same supplier.
            Func<T> registration = () => supplier();
            CapabilityOwnership.Register(supplier, () => providers.Add(registration),
                () => providers.Remove(registration));
        }
    }

    public static void RegisterPlayerCapability<T>(PlayerCapability<T> capability,
        Func<CCSPlayerController, T> supplier)
    {
        lock (CapabilityOwnership.Sync)
        {
            if (!PlayerCapability<T>.Providers.TryGetValue(capability.Name, out var providers))
                PlayerCapability<T>.Providers.Add(capability.Name, providers = new());
            Func<CCSPlayerController, T> registration = player => supplier(player);
            CapabilityOwnership.Register(supplier, () => providers.Add(registration),
                () => providers.Remove(registration));
        }
    }
}
