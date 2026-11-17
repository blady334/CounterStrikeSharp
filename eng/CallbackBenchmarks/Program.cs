using System.Diagnostics;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging.Abstractions;

// Offline microbenchmark of the actual reverse-P/Invoke callback entry.
// This measures dispatch, not server tick rate or gameplay performance.
unsafe class Program
{
    static long sink;
    static void Main()
    {
        _ = new Application(NullLoggerFactory.Instance, null!, null!, null!, null!, null!, null!, null!, null!);
        Run("Action", new Action(() => sink++));
        Run("Primitives", new Func<int, IntPtr, int>((a, b) => a + (int)b));
        Run("Arity1", new Func<int, int>(a => a));
        Run("Arity3", new Func<int, int, int, int>((a, b, c) => a + b + c));
        Run("Arity4", new Func<int, int, int, int, int>((a, b, c, d) => a + b + c + d));
        Run("Arity5", new Func<int, int, int, int, int, int>((a, b, c, d, e) => a + b + c + d + e));
        Run("ScriptContext", new Func<ScriptContext, int>(c => c.GetArgument<int>(0)));
        GC.KeepAlive(sink);
    }

    static void Run(string name, Delegate handler)
    {
        const int count = 100000;
        var reference = FunctionReference.Create(handler);
        try
        {
            var callback = (delegate* unmanaged<fxScriptContext*, void>)reference.GetFunctionPointer();
            fxScriptContext context = default;
            context.numArguments = 5;
            for (int i = 0; i < 5; i++) ((long*)context.functionData)[i] = i + 1;
            for (int i = 0; i < count; i++) callback(&context);
            var samples = new List<object>();
            for (int sample = 0; sample < 5; sample++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (int i = 0; i < count; i++) callback(&context);
                var elapsed = Stopwatch.GetTimestamp() - start;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                samples.Add(new { ns_per_call = elapsed * 1e9 / Stopwatch.Frequency / count, bytes_per_call = allocated / (double)count });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { name, calls_per_sample = count, samples }));
        }
        finally { FunctionReference.Remove(reference.Identifier); }
    }
}
