"""Exercise actual TimerSystem code with engine/callback boundaries stubbed."""
import argparse
import os
from pathlib import Path
import subprocess
ROOT = Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--out',required=True,type=Path)
args=parser.parse_args(); args.out.mkdir(parents=True,exist_ok=True)
header=(ROOT/'src/core/timer_system.h').read_text()
header='\n'.join(l for l in header.splitlines() if not l.startswith('#include "') and l!='#pragma once')
cpp=(ROOT/'src/core/timer_system.cpp').read_text()
def method(signature):
    start=cpp.index(signature); brace=cpp.index('{',start); depth=0
    for end in range(brace,len(cpp)):
        depth+=(cpp[end]=='{')-(cpp[end]=='}')
        if not depth: return cpp[start:end+1]
    raise ValueError(signature)
source=r'''
#include <algorithm>
#include <cassert>
#include <cstdio>
#include <memory>
#include <vector>
namespace counterstrikesharp {
class GlobalClass { public: virtual void OnAllInitialized() {} virtual void OnShutdown() {} virtual void OnLevelEnd() {} };
using CallbackT=void(*)();
struct Context { void Reset() {} };
class ScriptCallback { public: CallbackT callback=nullptr; Context context;
 void AddListener(CallbackT f) {callback=f;} Context& ScriptContext(){return context;}
 void Execute(){callback();} };
namespace globals {
 struct Manager { int live=0; ScriptCallback* CreateCallback(const char*){++live; return new ScriptCallback;}
 void ReleaseCallback(ScriptCallback* c){if(c){--live;delete c;}} } callbackManager;
 const double engine_fixed_tick_interval=0.015625;
}
}
'''+header+'\nnamespace counterstrikesharp { namespace timers { double universal_time=0; }\n'
for sig in ['timers::Timer::Timer(', 'timers::Timer::~Timer()', 'TimerSystem::TimerSystem()',
            'double TimerSystem::CalculateNextThink(', 'void TimerSystem::RunFrame()',
            'void TimerSystem::RemoveMapChangeTimers()', 'timers::Timer* TimerSystem::CreateTimer(',
            'void TimerSystem::KillTimer(']: source+=method(sig)+'\n'
source+='void TimerSystem::OnAllInitialized() {} void TimerSystem::OnShutdown() {} void TimerSystem::OnLevelEnd() {}\n}\n'
source+=r'''
using namespace counterstrikesharp;
TimerSystem systemUnderTest;
timers::Timer *first=nullptr,*second=nullptr;
int calls=0; bool nested=false;
void reenter() { ++calls; if(!nested) { nested=true; systemUnderTest.RunFrame(); nested=false; } }
void count() { ++calls; }
void killSelf() { ++calls; systemUnderTest.KillTimer(first); }
void changeMap() { ++calls; systemUnderTest.RemoveMapChangeTimers(); }
void killSibling() { ++calls; systemUnderTest.KillTimer(second); }
void addTimer() { ++calls; systemUnderTest.CreateTimer(0,count,0); }
int main() {
 first=systemUnderTest.CreateTimer(0,reenter,TIMER_FLAG_REPEAT);
 systemUnderTest.RunFrame(); assert(calls==1); systemUnderTest.KillTimer(first);
 calls=0; second=systemUnderTest.CreateTimer(0,count,TIMER_FLAG_REPEAT);
 first=systemUnderTest.CreateTimer(0,reenter,TIMER_FLAG_REPEAT);
 systemUnderTest.RunFrame(); assert(calls==2); systemUnderTest.KillTimer(first); systemUnderTest.KillTimer(second);
 calls=0; first=systemUnderTest.CreateTimer(0,killSelf,TIMER_FLAG_REPEAT);
 systemUnderTest.RunFrame(); systemUnderTest.RunFrame(); assert(calls==1);
 calls=0; second=systemUnderTest.CreateTimer(0,count,TIMER_FLAG_REPEAT|TIMER_FLAG_NO_MAPCHANGE);
 first=systemUnderTest.CreateTimer(0,changeMap,TIMER_FLAG_REPEAT|TIMER_FLAG_NO_MAPCHANGE);
 systemUnderTest.RunFrame(); systemUnderTest.RunFrame(); assert(calls==1);
 calls=0; second=systemUnderTest.CreateTimer(0,count,0);
 first=systemUnderTest.CreateTimer(0,killSibling,0);
 systemUnderTest.RunFrame(); assert(calls==1);
 calls=0; systemUnderTest.CreateTimer(0,addTimer,0);
 systemUnderTest.RunFrame(); assert(calls==1); systemUnderTest.RunFrame(); assert(calls==2);
 assert(globals::callbackManager.live==0);
 puts("PASS: timer reentry, self/sibling cancellation, map cleanup during callback, deferred additions, callback lifetime");
}
'''
path=args.out.resolve()/'timer-safety.cpp'; path.write_text(source)
exe=path.with_suffix('.exe' if os.name=='nt' else '')
command=['cl','/nologo','/EHsc','/std:c++20',str(path),'/Fe:'+str(exe),'/Fo:'+str(path.with_suffix('.obj'))] if os.name=='nt' else [
    'c++','-std=c++20','-g','-fsanitize=address,undefined','-fno-omit-frame-pointer',str(path),'-o',str(exe)]
subprocess.run(command,check=True); subprocess.run([str(exe)],check=True)
