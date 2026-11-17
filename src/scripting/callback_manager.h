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

#pragma once

#include <vector>
#include <memory>

#include "core/global_listener.h"
#include "core/globals.h"
#include "scripting/script_engine.h"

namespace counterstrikesharp {

class ScriptCallback : public std::enable_shared_from_this<ScriptCallback>
{
    struct ListenerState { CallbackT function; };

  public:
    // Snapshots keep the callback alive, but removal invalidates registrations
    // immediately. Re-adding the same function creates a different registration.
    class Listener
    {
      public:
        explicit Listener(std::shared_ptr<ListenerState> state) : m_state(std::move(state)) {}
        explicit operator bool() const { return m_state->function != nullptr; }
        void operator()(fxNativeContext* context) const
        {
            if (auto function = m_state->function) function(context);
        }
      private:
        std::shared_ptr<ListenerState> m_state;
    };
    struct Snapshot
    {
        std::shared_ptr<ScriptCallback> owner;
        std::shared_ptr<const std::vector<Listener>> listeners;
        const std::vector<Listener>& Entries() const
        {
            static const std::vector<Listener> empty;
            return listeners ? *listeners : empty;
        }
        auto begin() const { return Entries().begin(); }
        auto end() const { return Entries().end(); }
    };
    ScriptCallback(const char* szName);
    ~ScriptCallback();
    void AddListener(CallbackT fnPluginFunction);
    bool RemoveListener(CallbackT fnPluginFunction);
    bool IsContextSafe();
    std::string GetName() { return m_name; }
    unsigned int GetFunctionCount() const { return m_functions.size(); }
    Snapshot GetFunctions();
    void RemoveAllListeners();

    void Execute(bool bResetContext = true);
    void Reset();
    ScriptContextRaw& ScriptContext() { return m_script_context_raw; }
    fxNativeContext& ScriptContextStruct() { return m_root_context; }

  private:
    std::vector<std::shared_ptr<ListenerState>> m_functions;
    // Published lists are immutable. Nested dispatch can retain an older list;
    // the shared ListenerState tombstones still invalidate removed entries.
    std::shared_ptr<const std::vector<Listener>> m_snapshot;
    std::string m_name;
    std::string m_profile_name;
    ScriptContextRaw m_script_context_raw;
    fxNativeContext m_root_context;
};

class CallbackManager : public GlobalClass
{
  public:
    CallbackManager();

    ScriptCallback* CreateCallback(const char* szName);
    ScriptCallback* FindCallback(const char* szName);
    void ReleaseCallback(ScriptCallback* pCallback);
    bool TryAddFunction(const char* szName, CallbackT fnCallable);
    bool TryRemoveFunction(const char* szName, CallbackT fnCallable);
    void PrintCallbackDebug();

  private:
    std::vector<std::shared_ptr<ScriptCallback>> m_managed;
};

class CallbackPair
{
  public:
    CallbackPair();
    CallbackPair(bool bNoCallbacks);
    ~CallbackPair();
    bool HasCallbacks() const { return pre->GetFunctionCount() > 0 || post->GetFunctionCount() > 0; }

    ScriptCallback* pre = nullptr;
    ScriptCallback* post = nullptr;
};

} // namespace counterstrikesharp
