"""Exercise the actual startup/shutdown method bodies with engine boundaries stubbed.

This is an offline behavioral test, not a substitute for a CS2 map/Workshop test.
Builds with MSVC in a developer shell, or with a C++17 compiler on Linux.
"""
import argparse
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def body(path, signature):
    text = (ROOT / path).read_text(encoding='utf-8')
    start = text.index('{', text.index(signature))
    depth = 0
    for index in range(start, len(text)):
        depth += (text[index] == '{') - (text[index] == '}')
        if depth == 0:
            return text[start:index + 1]
    raise ValueError(signature)

parser = argparse.ArgumentParser()
parser.add_argument('--cxx', default='cl' if os.name == 'nt' else 'c++')
parser.add_argument('--out', required=True, type=Path)
args = parser.parse_args()
args.out.mkdir(parents=True, exist_ok=True)
timer = body('src/core/timer_system.cpp', 'void TimerSystem::OnStartupServer(')
startup = body('src/mm_plugin.cpp', 'CounterStrikeSharpMMPlugin::Hook_StartupServer(')
shutdown = body('src/mm_plugin.cpp', 'void CounterStrikeSharpMMPlugin::OnLevelShutdown(')
source = r'''
#include <cassert>
#include <cstdio>
#include <set>
#define CSSHARP_CORE_TRACE(...) ((void)0)
int ended = 0;
#define CALL_GLOBAL_LISTENER(...) (++ended)
namespace KHook { enum class Action { Ignore }; template<class T> struct Return { Action action; }; }
struct Context { void Reset() {} void Push(const char*) {} };
struct Callback {
    Context context; int calls=0; bool subscribed=true;
    Context& ScriptContext() { return context; }
    int GetFunctionCount() { return subscribed ? 1 : 0; }
    void Execute() { ++calls; }
};
struct TimerSystem {
    bool m_has_map_ticked=false, m_has_map_simulated=false;
    void OnStartupServer(bool levelShutdown);
};
struct EntitySystem {
    std::multiset<void*> listeners;
    void RemoveListenerEntity(void* p) { listeners.erase(p); }
    void AddListenerEntity(void* p) { listeners.insert(p); }
};
EntitySystem systemInstance;
EntitySystem* GameEntitySystem() { return &systemInstance; }
namespace globals {
    EntitySystem* entitySystem=nullptr;
    struct EntityManager { int entityListener=0; } entityManager;
    struct Vars { struct Name { const char* ToCStr() { return "test"; } } mapname; } vars;
    Vars* getGlobalVars() { return &vars; }
    TimerSystem timerSystem;
}
struct Plugin {
    bool m_level_shutdown_pending=false, m_has_level_initialized=false;
    Callback* on_activate_callback; Callback* on_map_end_callback;
    KHook::Return<void> Startup(); void OnLevelShutdown();
};
'''
source += '\nvoid TimerSystem::OnStartupServer(bool levelShutdown) ' + timer
source += '\nKHook::Return<void> Plugin::Startup() ' + startup
source += '\nvoid Plugin::OnLevelShutdown() ' + shutdown
source += r'''
int main() {
    Callback activated, mapEnd;
    Plugin plugin; plugin.on_activate_callback=&activated; plugin.on_map_end_callback=&mapEnd;
    plugin.Startup();
    assert(ended==0 && systemInstance.listeners.size()==1 && activated.calls==1);
    // Workshop/HLTV repeats StartupServer without LevelShutdown.
    for (int i=0;i<5;i++) {
        globals::timerSystem.m_has_map_ticked=true;
        globals::timerSystem.m_has_map_simulated=true;
        plugin.Startup();
        assert(ended==0 && systemInstance.listeners.size()==1);
        assert(!globals::timerSystem.m_has_map_ticked && !globals::timerSystem.m_has_map_simulated);
    }
    // A real map shutdown still emits the existing map-end callback once.
    plugin.m_has_level_initialized=true;
    globals::timerSystem.m_has_map_ticked=true;
    plugin.OnLevelShutdown(); plugin.OnLevelShutdown();
    assert(mapEnd.calls==1);
    plugin.Startup();
    assert(ended==1 && !plugin.m_level_shutdown_pending);
    globals::timerSystem.m_has_map_ticked=true;
    plugin.Startup();
    assert(ended==1 && systemInstance.listeners.size()==1);
    // No ticks in a short-lived session: no duplicate internal level-end notification.
    plugin.m_has_level_initialized=true;
    plugin.OnLevelShutdown(); plugin.Startup();
    assert(ended==1 && mapEnd.calls==2);
    std::puts("PASS: initial start, repeated Workshop starts, listener deduplication, timer reset, real map end, short session");
}
'''
cpp = args.out.resolve() / 'startup-lifecycle.cpp'
exe = args.out.resolve() / ('startup-lifecycle.exe' if os.name == 'nt' else 'startup-lifecycle')
cpp.write_text(source, encoding='utf-8')
if Path(args.cxx).stem.lower() in ('cl', 'clang-cl'):
    command = [args.cxx, '/nologo', '/EHsc', '/std:c++17', str(cpp), '/Fe:' + str(exe), '/Fo:' + str(cpp.with_suffix('.obj'))]
else:
    command = [args.cxx, '-std=c++17', str(cpp), '-o', str(exe)]
subprocess.run(command, check=True)
subprocess.run([str(exe)], check=True)
