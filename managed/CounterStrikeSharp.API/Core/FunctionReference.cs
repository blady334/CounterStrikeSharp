/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Describes the lifetime of a function reference.
    /// </summary>
    public enum FunctionLifetime
    {
        /// <summary>Delegate will be removed after the first invocation.</summary>
        SingleUse,

        /// <summary>Delegate will remain in memory for the lifetime of the application (or until <see cref="FunctionReference.Remove"/> is called).</summary>
        Permanent
    }

    /// <summary>
    /// Represents a reference to a function that can be called from native code.
    /// </summary>
    public class FunctionReference
    {
        public unsafe delegate void CallbackDelegate(fxScriptContext* context);

        private static readonly ConcurrentDictionary<int, FunctionReference> IdToFunctionReferencesMap = new();
        private static readonly ConcurrentDictionary<Delegate, FunctionReference> TargetMethodToFunctionReferencesMap = new();

        private static readonly object ReferenceCounterLock = new();
        private static int _referenceCounter;

        private static readonly ConditionalWeakTable<Type, MethodInvoker> Invokers = new();

        private readonly Delegate _targetMethod;
        private readonly MethodInvoker _invoker;
        private readonly Type[] _parameterTypes;
        private readonly bool _takesScriptContext;
        private readonly CallbackDelegate _nativeCallback;

        private readonly TaskCompletionSource _taskCompletionSource = new();

        private FunctionReference(Delegate method, FunctionLifetime lifetime)
        {
            Lifetime = lifetime;
            _targetMethod = method;
            _invoker = Invokers.GetValue(method.GetType(), static type => MethodInvoker.Create(type.GetMethod("Invoke")!));
            _parameterTypes = Array.ConvertAll(method.GetType().GetMethod("Invoke")!.GetParameters(), parameter => parameter.ParameterType);
            _takesScriptContext = _parameterTypes.FirstOrDefault() == typeof(ScriptContext);
            _nativeCallback = CreateWrappedCallback();
        }

        /// <summary>
        /// <inheritdoc cref="FunctionLifetime"/>
        /// </summary>
        public FunctionLifetime Lifetime { get; }

        /// <summary>
        /// For <see cref="FunctionLifetime.SingleUse"/> function references, this task will complete when
        /// the function has finished invoking.
        /// </summary>
        public Task CompletionTask => _taskCompletionSource.Task;

        public int Identifier { get; private set; }

        private unsafe CallbackDelegate CreateWrappedCallback()
        {
            return context =>
            {
                var diagnosticStart = RuntimeDiagnostics.Begin();
                try
                {
                    var scriptContext = new ScriptContext(context);

                    // Allow for manual handling of the script context
                    if (_takesScriptContext)
                    {
                        var returnValue = _invoker.Invoke(_targetMethod, scriptContext);
                        if (returnValue != null)
                        {
                            scriptContext.SetResult(returnValue, context);
                        }

                        return;
                    }

                    var arguments = _parameterTypes.Length == 0 ? Array.Empty<object?>() : new object?[_parameterTypes.Length];
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        arguments[i] = scriptContext.GetArgument(_parameterTypes[i], i);
                    }

                    var returnObj = _invoker.Invoke(_targetMethod, arguments.AsSpan());

                    if (returnObj != null)
                    {
                        scriptContext.SetResult(returnObj, context);
                    }
                }
                catch (Exception e)
                {
                    if (e is Plugin.PluginTerminationException)
                    {
                        return;
                    }

                    Application.Instance.Logger.LogError(e,
                        "Error invoking callback {CallbackId}: assembly={Assembly}, method={DeclaringType}.{Method}",
                        Identifier, _targetMethod.Method.Module.Assembly.GetName().Name,
                        _targetMethod.Method.DeclaringType?.FullName, _targetMethod.Method.Name);
                }
                finally
                {
                    if (diagnosticStart != 0) RuntimeDiagnostics.End(_targetMethod, diagnosticStart, "callback");
                    if (Lifetime == FunctionLifetime.SingleUse)
                    {
                        RemoveSelf();
                    }

                    _taskCompletionSource.TrySetResult();
                }
            };
        }

        public static FunctionReference Create(Delegate method, FunctionLifetime lifetime = FunctionLifetime.Permanent)
        {
            // We always want to create a new reference if the lifetime is single use.
            if (lifetime == FunctionLifetime.Permanent && TargetMethodToFunctionReferencesMap.TryGetValue(method, out var existingReference))
            {
                return existingReference;
            }

            var reference = new FunctionReference(method, lifetime);
            var referenceId = Register(reference);

            reference.Identifier = referenceId;

            return reference;
        }


        // Each native subscription owns its callback independently, even when
        // the same managed handler is registered for several events or modes.
        internal static FunctionReference CreateUnique(Delegate method)
        {
            var reference = new FunctionReference(method, FunctionLifetime.Permanent);
            reference.Identifier = Register(reference, cacheByTarget: false);
            return reference;
        }

        private static int Register(FunctionReference reference, bool cacheByTarget = true)
        {
            lock (ReferenceCounterLock)
            {
                var thisRefId = _referenceCounter;
                IdToFunctionReferencesMap[thisRefId] = reference;
                if (cacheByTarget)
                    TargetMethodToFunctionReferencesMap[reference._targetMethod] = reference;

                unchecked
                {
                    _referenceCounter++;
                }

                return thisRefId;
            }
        }

        public IntPtr GetFunctionPointer() => Marshal.GetFunctionPointerForDelegate(_nativeCallback);

        private void RemoveSelf()
        {
            Remove(Identifier);
        }

        public static void Remove(int reference)
        {
            if (IdToFunctionReferencesMap.TryRemove(reference, out var functionReference))
            {
                // Do not evict another subscription/reference for the same target.
                ((ICollection<KeyValuePair<Delegate, FunctionReference>>)TargetMethodToFunctionReferencesMap)
                    .Remove(new(functionReference._targetMethod, functionReference));

                Application.Instance.Logger.LogDebug("Removing function/callback reference: {Reference}", reference);
            }
        }
    }
}
