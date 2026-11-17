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

#include "scripting/callback_manager.h"

#include <algorithm>

#include "core/log.h"
#include "vprof.h"

namespace counterstrikesharp {

ScriptCallback::ScriptCallback(const char* szName) : m_root_context(fxNativeContext{})
{
    m_script_context_raw = ScriptContextRaw(m_root_context);
    m_name = std::string(szName);
    m_profile_name = "ScriptCallback::Execute::" + m_name;
}

ScriptCallback::~ScriptCallback() { m_functions.clear(); }

void ScriptCallback::AddListener(CallbackT fnPluginFunction)
{
    if (!fnPluginFunction) return;
    m_functions.push_back(std::make_shared<ListenerState>(ListenerState{fnPluginFunction}));
    m_snapshot.reset();
}

bool ScriptCallback::RemoveListener(CallbackT fnPluginFunction)
{
    size_t nOriginalSize = m_functions.size();
    std::erase_if(m_functions, [fnPluginFunction](const auto& listener) {
        if (listener->function != fnPluginFunction) return false;
        listener->function = nullptr;
        return true;
    });
    const bool changed = m_functions.size() != nOriginalSize;
    if (changed) m_snapshot.reset();
    return changed;
}

void ScriptCallback::RemoveAllListeners()
{
    for (const auto& listener : m_functions) listener->function = nullptr;
    m_functions.clear();
    m_snapshot.reset();
}

ScriptCallback::Snapshot ScriptCallback::GetFunctions()
{
    if (m_functions.empty()) return Snapshot{shared_from_this(), {}};
    if (!m_snapshot)
    {
        auto listeners = std::make_shared<std::vector<Listener>>();
        listeners->reserve(m_functions.size());
        for (const auto& listener : m_functions) listeners->emplace_back(listener);
        m_snapshot = std::move(listeners);
    }
    return Snapshot{shared_from_this(), m_snapshot};
}

bool ScriptCallback::IsContextSafe()
{
    try
    {
        auto& Ctx = ScriptContext();
        Ctx.GetResult<void*>();
        return true;
    }
    catch (...)
    {
        CSSHARP_CORE_WARN("Context is invalid (exception during access)");
        return false;
    }
}

void ScriptCallback::Execute(bool bResetContext)
{
    if (!IsContextSafe())
    {
        ScriptContext().ThrowNativeError("ScriptCallback::Execute aborted due to invalid context");
        CSSHARP_CORE_WARN("ScriptCallback::Execute aborted due to invalid context (callback: '{}')", m_name);
        return;
    }

    VPROF_BUDGET("CS#::ScriptCallback::Execute", "CS# Script Callbacks");

    auto functions = GetFunctions();
    // A nested call prepares m_root_context again. The current dispatch owns a
    // separate buffer so its arguments survive nested calls and callback removal.
    fxNativeContext invocation = m_root_context;
    ScriptContextRaw context(invocation);
    size_t nI = 0;
    for (const auto& fnMethodToCall : functions)
    {
        if (fnMethodToCall)
        {
            try
            {
                fnMethodToCall(&invocation);
            }
            catch (...)
            {
                if (!context.HasError()) context.ThrowNativeError("Exception in callback execution");
                CSSHARP_CORE_ERROR("Exception thrown inside callback '{}', index {}", m_name, nI);
            }
        }
        ++nI;
    }

    if (bResetContext)
    {
        context.Reset();
    }
    m_root_context = invocation;
    m_script_context_raw = ScriptContextRaw(m_root_context);
}

void ScriptCallback::Reset() { ScriptContext().Reset(); }

CallbackManager::CallbackManager() = default;

ScriptCallback* CallbackManager::CreateCallback(const char* szName)
{
    CSSHARP_CORE_TRACE("Creating callback {0}", szName);
    auto pCallback = std::make_shared<ScriptCallback>(szName);
    m_managed.push_back(pCallback);

    return pCallback.get();
}

ScriptCallback* CallbackManager::FindCallback(const char* szName)
{
    for (const auto& pMarshal : m_managed)
    {
        if (strcmp(pMarshal->GetName().c_str(), szName) == 0)
        {
            return pMarshal.get();
        }
    }

    return nullptr;
}

void CallbackManager::ReleaseCallback(ScriptCallback* pCallback)
{
    auto found = std::find_if(m_managed.begin(), m_managed.end(), [pCallback](const auto& owned) {
        return owned.get() == pCallback;
    });
    if (found == m_managed.end()) return;
    (*found)->RemoveAllListeners();
    m_managed.erase(found);
}

bool CallbackManager::TryAddFunction(const char* szName, CallbackT fnCallable)
{
    if (auto* pCallback = FindCallback(szName))
    {
        pCallback->AddListener(fnCallable);
        return true;
    }

    return false;
}

bool CallbackManager::TryRemoveFunction(const char* szName, CallbackT fnCallable)
{
    if (auto* pCallback = FindCallback(szName))
    {
        return pCallback->RemoveListener(fnCallable);
    }

    return false;
}

void CallbackManager::PrintCallbackDebug()
{
    CSSHARP_CORE_INFO("----CALLBACKS----");
    for (const auto& pCallback : m_managed)
    {
        CSSHARP_CORE_INFO("{0} ({0})\n", pCallback->GetName().c_str(), 1);
    }
}
CallbackPair::CallbackPair()
{
    pre = globals::callbackManager.CreateCallback("");
    post = globals::callbackManager.CreateCallback("");
}

CallbackPair::CallbackPair(bool bNoCallbacks)
{
    if (!bNoCallbacks)
    {
        pre = globals::callbackManager.CreateCallback("");
        post = globals::callbackManager.CreateCallback("");
    }
}

CallbackPair::~CallbackPair()
{
    globals::callbackManager.ReleaseCallback(pre);
    globals::callbackManager.ReleaseCallback(post);
}

} // namespace counterstrikesharp
