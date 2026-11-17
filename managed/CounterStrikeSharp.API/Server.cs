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

using System.Threading.Tasks;
using System.Collections.Concurrent;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API
{
    public class Server
    {
        internal static void Initialize()
        {
            NativeAPI.AddListener("OnMapEnd", (Delegate)(() => OnMapEnd()));
            NativeAPI.AddListener("OnMapStart", (Delegate)((string map) => OnMapStart()));
            NativeAPI.AddListener("OnTick", (Delegate)(() => OnTick()));
            NativeAPI.AddListener("OnServerPreWorldUpdate", (Delegate)((bool simulating) => OnWorldUpdate()));
        }

        internal static readonly DeferredWorkScheduler Work = new();
        private static bool _mapStarted;
        internal static void OnMapStart()
        {
            // Workshop can restart the tick counter without an OnMapEnd event.
            // Preserve ordinary frame/world work and first-start initialization.
            if (_mapStarted) Work.CancelTickWork();
            _mapStarted = true;
        }
        private static readonly ConcurrentQueue<Action> Cleanup = new();
        internal static void EnqueueCleanup(Action cleanup) => Cleanup.Enqueue(cleanup);
        private static void DrainCleanup()
        {
            int count = Cleanup.Count;
            while (count-- > 0 && Cleanup.TryDequeue(out var cleanup))
            {
                try { cleanup(); }
                catch (Exception error) { Application.Instance.Logger.LogError(error, "Native resource cleanup failed"); }
            }
        }

        internal static void OnTick()
        {
            if (Work.HasTickWork) Work.Drain(WorkLane.Tick, CoreConfig.MaximumFrameTasksExecutedPerTick, TickCount);
            Work.Drain(WorkLane.Frame, CoreConfig.MaximumFrameTasksExecutedPerTick);
            if (CoreConfig.RuntimeDiagnosticsEnabled) RuntimeDiagnostics.Report(Work.Counts);
        }

        internal static void OnWorldUpdate()
        {
            DrainCleanup();
            Work.Drain(WorkLane.World, CoreConfig.MaximumFrameTasksExecutedPerTick);
            if (CoreConfig.RuntimeDiagnosticsEnabled) RuntimeDiagnostics.Report(Work.Counts);
        }

        internal static void OnMapEnd()
        {
            DrainCleanup();
            Work.CancelMapWork();
            Modules.Timers.Timer.StopMapTimers();
        }

        private static Task QueueAction(Action task, WorkLane lane, int tick = 0, bool logFailure = false)
        {
            ArgumentNullException.ThrowIfNull(task);
            return Work.Enqueue(() => { task(); return true; }, task, lane, tick, logFailure);
        }

        /// <summary>
        /// Duration of a single game tick in seconds, based on a 64 tick server (hard coded in CS2).
        /// </summary>
        public static float TickInterval => 0.015625f;

        /// <summary>
        /// Executes a command on the server, as if it was entered from the console.
        /// </summary>
        /// <param name="command"></param>
        public static void ExecuteCommand(string command) => NativeAPI.IssueServerCommand(command);

        public static string MapName => NativeAPI.GetMapName();

        /// <summary>
        /// Returns the total time the server has been running in seconds.
        /// </summary>
        /// <remarks>Does not increment when server is hibernating</remarks>
        public static double TickedTime => NativeAPI.GetTickedTime();

        /// <summary>
        /// Returns the current map time in seconds, as an interval of the server's tick interval.
        /// e.g. 70.046875 would represent 70 seconds of map time and the 4483rd tick of the server (70.046875 / 0.015625).
        /// </summary>
        /// <remarks>Increments even when server is hibernating</remarks>
        public static float CurrentTime => NativeAPI.GetCurrentTime();

        /// <summary>
        /// Returns the current map tick count.
        /// CS2 is a 64 tick server, so the value will increment by 64 every second.
        /// </summary>
        public static int TickCount => NativeAPI.GetTickCount();

        /// <summary>
        /// Returns the total time the server has been running in seconds.
        /// </summary>
        /// <remarks>Increments even when server is hibernating</remarks>
        public static double EngineTime => NativeAPI.GetEngineTime();

        /// <summary>
        /// Returns the time spent on last server or client frame
        /// </summary>
        public static float FrameTime => NativeAPI.GetGameFrameTime();

        public static void PrecacheModel(string name) => NativeAPI.PrecacheModel(name);

        /// <summary>
        /// <inheritdoc cref="RunOnTick"/>
        /// Returns Task that completes once the synchronous task has been completed.
        /// </summary>
        public static Task RunOnTickAsync(int tick, Action task)
        {
            return QueueAction(task, WorkLane.Tick, tick);
        }

        /// <summary>
        /// Queue a task to be executed on the specified tick.
        /// See <see cref="TickCount"/> to retrieve the current tick.
        /// <remarks>Does not execute if the server is hibernating.</remarks>
        /// </summary>
        public static void RunOnTick(int tick, Action task)
        {
            QueueAction(task, WorkLane.Tick, tick, logFailure: true);
        }

        /// <summary>
        /// <inheritdoc cref="NextFrame"/>
        /// Returns Task that completes once the synchronous task has been completed.
        /// </summary>
        public static Task NextFrameAsync(Action task)
        {
            return QueueAction(task, WorkLane.Frame);
        }

        /// <summary>
        /// <inheritdoc cref="NextFrame"/>
        /// <returns>A <see cref="Task{TResult}"/> that will be completed when the function has finished executing.</returns>
        /// </summary>
        public static Task<TResult> NextFrameAsync<TResult>(Func<TResult> task)
        {
            return Work.Enqueue(task, task, WorkLane.Frame);
        }

        /// <summary>
        /// Queue a task to be executed on the next game frame.
        /// <remarks>Does not execute if the server is hibernating.</remarks>
        /// </summary>
        public static void NextFrame(Action task)
        {
            QueueAction(task, WorkLane.Frame, logFailure: true);
        }

        /// <summary>
        /// <inheritdoc cref="NextWorldUpdate"/>
        /// Returns Task that completes once the synchronous task has been completed.
        /// </summary>
        public static Task NextWorldUpdateAsync(Action task)
        {
            return QueueAction(task, WorkLane.World);
        }

        /// <summary>
        /// <inheritdoc cref="NextWorldUpdate"/>
        /// <returns>A <see cref="Task{TResult}"/> that will be completed when the function has finished executing.</returns>
        /// </summary>
        public static Task<TResult> NextWorldUpdateAsync<TResult>(Func<TResult> task)
        {
            return Work.Enqueue(task, task, WorkLane.World);
        }

        /// <summary>
        /// Queue a task to be executed on the next pre world update.
        /// <remarks>Executes if the server is hibernating.</remarks>
        /// </summary>
        /// <param name="task"></param>
        public static void NextWorldUpdate(Action task)
        {
            QueueAction(task, WorkLane.World, logFailure: true);
        }

        public static void PrintToChatAll(string message)
        {
            VirtualFunctions.ClientPrintAll(HudDestination.Chat, message, 0, 0, 0, 0, 0);
        }

        public static string GameDirectory => NativeAPI.GetGameDirectory();

        private static int? _maxPlayers;

        public static int MaxPlayers
        {
            get
            {
                _maxPlayers ??= NativeAPI.GetMaxClients();

                return _maxPlayers.Value;
            }
        }

        public static bool IsMapValid(string mapName) => NativeAPI.IsMapValid(mapName);

        public static string[] GetMapList()
        {
            var mapListPath = Path.Join(GameDirectory, "maplist.txt");
            if (!File.Exists(mapListPath)) throw new Exception("Maplist.txt could not be found!");

            var mapNames = File.ReadAllLines(mapListPath);

            return mapNames.Where(map =>
            {
                var filePath = Path.Join(GameDirectory, "maps", $"{map}.bsp");
                return File.Exists(filePath) && IsMapValid(map);
            }).ToArray();
        }

        public static void PrintToConsole(string s) => NativeAPI.PrintToServerConsole($"{s}\n\0");
    }
}
