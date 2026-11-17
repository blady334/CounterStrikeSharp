"""Run the actual callback/context implementation without loading CS2.

Only engine logging, profiling and GlobalClass are stubbed. Linux additionally
runs AddressSanitizer/UndefinedBehaviorSanitizer to detect lifetime violations.
"""
import argparse
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def method(path, signature):
    text = (ROOT / path).read_text(encoding='utf-8')
    start = text.index(signature)
    brace = text.index('{', start)
    depth = 0
    for end in range(brace, len(text)):
        depth += (text[end] == '{') - (text[end] == '}')
        if depth == 0:
            return text[start:end + 1]
    raise ValueError(signature)

parser = argparse.ArgumentParser()
parser.add_argument('--out', type=Path, required=True)
parser.add_argument('--cxx', default='cl' if os.name == 'nt' else 'c++')
parser.add_argument('--benchmark', action='store_true', help='Optimized dispatch microbenchmark (no sanitizers)')
args = parser.parse_args()
args.out.mkdir(parents=True, exist_ok=True)
header = (ROOT / 'src/scripting/callback_manager.h').read_text()
header = '\n'.join(line for line in header.splitlines()
                   if not line.startswith('#include "core/') and line != '#pragma once')
implementation = (ROOT / 'src/scripting/callback_manager.cpp').read_text()
implementation = '\n'.join(line for line in implementation.splitlines() if not line.startswith('#include'))
source = r'''
#include <algorithm>
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <memory>
#include <stack>
#include <string>
#include <unordered_map>
#include <vector>
#define CSSHARP_CORE_WARN(...) ((void)0)
#define CSSHARP_CORE_ERROR(...) ((void)0)
#define CSSHARP_CORE_INFO(...) ((void)0)
#define CSSHARP_CORE_TRACE(...) ((void)0)
#define VPROF_BUDGET(...) ((void)0)
namespace counterstrikesharp { class GlobalClass {}; }
'''
source += header
source += '\nnamespace counterstrikesharp { namespace globals { CallbackManager callbackManager; } std::stack<std::string> errors;\n'
source += method('src/scripting/script_engine.cpp', 'void ScriptContext::ThrowNativeError(')
source += '\n' + method('src/scripting/script_engine.cpp', 'void ScriptContext::Reset(')
source += '\nstatic std::unordered_map<uint64_t, TNativeHandler> g_registeredHandlers;\n'
for signature in ['tl::optional<TNativeHandler> ScriptEngine::GetNativeHandler(uint64_t',
                  'void ScriptEngine::RegisterNativeHandlerInt(', 'void ScriptEngine::InvokeNative(']:
    source += '\n' + method('src/scripting/script_engine.cpp', signature)
source += '\n}\n'
source += implementation
if args.benchmark:
    source += '\n#define main safety_main\n'
source += r'''
using namespace counterstrikesharp;
CallbackManager manager;
ScriptCallback* current;
std::vector<int> seen;
bool nested = false;
void second(fxNativeContext* raw) { seen.push_back(ScriptContextRaw(*raw).GetArgument<int>(0)); }
void third(fxNativeContext*) { seen.push_back(3); }
void removeSelf(fxNativeContext*) { current->RemoveListener(removeSelf); seen.push_back(1); }
void removeNext(fxNativeContext*) { current->RemoveListener(second); seen.push_back(1); }
void addNext(fxNativeContext*) { current->AddListener(third); }
void replaceNext(fxNativeContext*) { current->RemoveListener(second); current->AddListener(second); }
void releaseAll(fxNativeContext*) { manager.ReleaseCallback(current); seen.push_back(1); }
void reenter(fxNativeContext*) {
    if (nested) return;
    nested = true;
    current->Reset(); current->ScriptContext().Push(99); current->Execute();
    nested = false;
}
void throwing(fxNativeContext*) { throw std::runtime_error("test"); }
void prepare() { current->Reset(); current->ScriptContext().Push(42); seen.clear(); }
void create() { current = manager.CreateCallback("test"); prepare(); }
void destroy() { manager.ReleaseCallback(current); }
int main() {
    // A retained snapshot must survive multiple publications. Old listeners
    // are invalidated immediately; replacements are visible only to new calls.
    create(); current->AddListener(second);
    {
        auto old = current->GetFunctions();
        current->AddListener(third);
        auto middle = current->GetFunctions();
        current->RemoveListener(second); current->AddListener(second);
        for (const auto& listener : old) if (listener) listener(&current->ScriptContextStruct());
        assert(seen.empty());
        for (const auto& listener : middle) if (listener) listener(&current->ScriptContextStruct());
        assert((seen == std::vector<int>{3}));
        prepare(); current->Execute(); assert((seen == std::vector<int>{3,42}));
        manager.ReleaseCallback(current);
        seen.clear();
        for (const auto& listener : middle) if (listener) listener(nullptr);
        assert(seen.empty());
    }
    { ScriptCallback::Snapshot empty; assert(empty.begin() == empty.end()); }
    create(); current->AddListener(removeSelf); current->AddListener(second); current->AddListener(third);
    current->Execute(); assert((seen == std::vector<int>{1,42,3})); destroy();
    create(); current->AddListener(removeNext); current->AddListener(second); current->AddListener(third);
    current->Execute(); assert((seen == std::vector<int>{1,3})); destroy();
    create(); current->AddListener(addNext); current->AddListener(second);
    current->Execute(); assert((seen == std::vector<int>{42}));
    prepare(); current->Execute(); assert((seen == std::vector<int>{42,3})); destroy();
    create(); current->AddListener(replaceNext); current->AddListener(second);
    current->Execute(); assert(seen.empty()); destroy();
    create(); current->AddListener(releaseAll); current->AddListener(second);
    current->Execute(); assert((seen == std::vector<int>{1}));
    create(); current->AddListener(reenter); current->AddListener(second);
    current->Execute(); assert((seen == std::vector<int>{99,42})); destroy();
    create(); current->AddListener(throwing); current->AddListener(second);
    current->Execute(); assert((seen == std::vector<int>{42})); assert(errors.empty()); destroy();
    create(); current->AddListener(releaseAll); current->AddListener(second);
    { auto listeners = current->GetFunctions();
      for (auto listener : listeners) { if (listener) { fxNativeContext raw{}; listener(&raw); } }
    }
    assert((seen == std::vector<int>{1}));
    create(); current->AddListener(second); current->Execute(false);
    assert((seen == std::vector<int>{42})); destroy();
    fxNativeContext raw{}; ScriptContextRaw context(raw);
    for (int repeat=0; repeat<100; ++repeat) {
        context.Reset(); assert(raw.numArguments==0 && raw.numResults==0);
        for (int i=0;i<32;++i) context.Push(i);
        assert(raw.numArguments==32 && context.GetArgument<int>(31)==31);
        raw.result=123;
        bool rejected=false; try { context.Push(33); } catch (const std::exception&) { rejected=true; }
        assert(rejected && raw.result==123 && raw.numArguments==32);
    }
    for (int index : {-1,32}) {
        bool rejected=false; try { context.GetArgument<int>(index); } catch (const std::exception&) { rejected=true; }
        assert(rejected);
    }
    context.Reset(); context.PushString("text"); assert(raw.numArguments==1);
    assert(std::string(context.GetArgument<const char*>(0))=="text");
    context.SetResult(7); assert(raw.numResults==1 && context.GetResult<int>()==7);
    context.Reset(); raw.nativeIdentifier=1;
    ScriptEngine::RegisterNativeHandlerInt(1, [](ScriptContext& c) { c.GetArgument<int>(0); });
    ScriptEngine::InvokeNative(raw);
    assert(raw.hasError==1 && std::string(context.GetResult<const char*>()).find("argument")!=std::string::npos);
    context.Reset(); assert(errors.empty());
    puts("PASS: callback mutation, release, reentry, exceptions, snapshots; argument bounds/reset/string counts");
    return 0;
}
'''
if args.benchmark:
    source += r'''
#undef main
#include <chrono>
volatile int bench_sink = 0;
void bench_listener(fxNativeContext*) { bench_sink = bench_sink + 1; }
int main() {
    safety_main();
    constexpr int count = 1000000;
    for (int listeners : {0, 1, 8, 32}) {
        auto* callback = manager.CreateCallback("bench");
        for(int i=0;i<listeners;++i) callback->AddListener(bench_listener);
        for(int i=0;i<10000;++i) callback->Execute();
        for(int sample=0;sample<5;++sample) {
            auto start=std::chrono::steady_clock::now();
            for(int i=0;i<count;++i) callback->Execute();
            auto ns=std::chrono::duration<double,std::nano>(std::chrono::steady_clock::now()-start).count()/count;
            printf("{\"listeners\":%d,\"sample\":%d,\"ns_per_call\":%.3f}\n",listeners,sample,ns);
        }
        manager.ReleaseCallback(callback);
    }
}
'''
cpp = args.out.resolve() / 'callback-safety.cpp'
exe = args.out.resolve() / ('callback-safety.exe' if os.name == 'nt' else 'callback-safety')
cpp.write_text(source, encoding='utf-8')
includes = [ROOT / 'src', ROOT / 'libraries/tl']
if Path(args.cxx).stem.lower() in ('cl', 'clang-cl'):
    command = [args.cxx, '/nologo', '/EHsc', '/std:c++20', *['/I'+str(p) for p in includes],
               str(cpp), '/Fe:'+str(exe), '/Fo:'+str(cpp.with_suffix('.obj'))]
    if args.benchmark: command += ['/O2']
else:
    command = [args.cxx, '-std=c++20', *(['-O2'] if args.benchmark else ['-g', '-fsanitize=address,undefined', '-fno-omit-frame-pointer']),
               *['-I'+str(p) for p in includes], str(cpp), '-o', str(exe)]
subprocess.run(command, check=True)
subprocess.run([str(exe)], check=True)
