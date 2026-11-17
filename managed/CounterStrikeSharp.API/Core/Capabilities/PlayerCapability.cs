using System.Collections.Generic;

namespace CounterStrikeSharp.API.Core.Capabilities;

public sealed class PlayerCapability<T>
{
    public string Name { get; }
    internal static readonly Dictionary<string, List<Func<CCSPlayerController, T>>> Providers = new();

    public PlayerCapability(string name)
    {
        Name = name;
    }

    public T? Get(CCSPlayerController entity)
    {
        Func<CCSPlayerController, T>? provider;
        lock (CapabilityOwnership.Sync)
            provider = Providers[Name].FirstOrDefault();
        return provider != null ? provider(entity) : default;
    }
}
