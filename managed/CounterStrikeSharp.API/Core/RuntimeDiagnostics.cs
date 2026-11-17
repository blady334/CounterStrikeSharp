using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core;

// Stores strings/numbers only: diagnostics must not pin collectible plugin types.
internal static class RuntimeDiagnostics
{
    private static readonly object Sync = new();
    internal readonly record struct Sample(string Name, long Count, double TotalMs, double MaxMs);
    private static readonly Dictionary<string, Sample> Samples = new();
    private static long _lastReport;
    private static int _dropped;
    internal static long Begin() => CoreConfig.RuntimeDiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
    internal static void End(Delegate callback, long started, string kind)
    {
        if (started == 0 || !CoreConfig.RuntimeDiagnosticsEnabled) return;
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms < CoreConfig.SlowCallbackMilliseconds) return;
        var method = callback.Method;
        Record($"{kind}:{method.Module.Assembly.GetName().Name}:{method.DeclaringType?.FullName}.{method.Name}", ms);
    }
    internal static void Record(string name, double ms)
    {
        lock (Sync)
        {
            if (!Samples.TryGetValue(name, out var old) && Samples.Count >= 128) { _dropped++; return; }
            Samples[name] = new(name, old.Count + 1, old.TotalMs + ms, Math.Max(old.MaxMs, ms));
        }
    }
    internal static Sample[] TakeSamples()
    {
        lock (Sync) { var result = Samples.Values.OrderByDescending(s => s.TotalMs).ToArray(); Samples.Clear(); return result; }
    }
    internal static void Reset()
    {
        lock (Sync) { Samples.Clear(); _dropped = 0; _lastReport = Stopwatch.GetTimestamp(); }
    }
    internal static void Report((int Frame, int World, int Tick) queues)
    {
        if (!CoreConfig.RuntimeDiagnosticsEnabled) return;
        Sample[] samples;
        int dropped;
        lock (Sync)
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastReport, now).TotalSeconds < CoreConfig.DiagnosticsIntervalSeconds) return;
            _lastReport = now;
            samples = TakeSamples(); dropped = _dropped; _dropped = 0;
        }
        if (samples.Length == 0 && queues.Frame + queues.World + queues.Tick < CoreConfig.DiagnosticsQueueThreshold) return;
        Application.Instance.Logger.LogInformation(
            "Runtime diagnostics: pending frame={Frame}, world={World}, tick={Tick}; slow callbacks={Slow}; untracked samples={Dropped}; top={Top}",
            queues.Frame, queues.World, queues.Tick, samples.Sum(s => s.Count), dropped,
            string.Join("; ", samples.Take(8).Select(s => $"{s.Name} count={s.Count} total_ms={s.TotalMs:F2} max_ms={s.MaxMs:F2}")));
    }
}
