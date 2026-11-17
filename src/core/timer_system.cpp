/**
 * =============================================================================
 * SourceMod
 * Copyright (C) 2004-2016 AlliedModders LLC.  All rights reserved.
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 * As a special exception, AlliedModders LLC gives you permission to link the
 * code of this program (as well as its derivative works) to "Half-Life 2," the
 * "Source Engine," the "SourcePawn JIT," and any Game MODs that run on software
 * by the Valve Corporation.  You must obey the GNU General Public License in
 * all respects for all other code used.  Additionally, AlliedModders LLC grants
 * this exception to all derivative works.  AlliedModders LLC defines further
 * exceptions, found in LICENSE.txt (as of this writing, version JULY-31-2007),
 * or <http://www.sourcemod.net/license.php>.
 *
 * This file has been modified from its original form, under the GNU General
 * Public License, version 3.0.
 */

#include "core/timer_system.h"

#include <engine2/ivengineserver2.h>

#include <algorithm>

#include "core/globals.h"
#include "core/log.h"
#include "core/managers/player_manager.h"
#include "scripting/callback_manager.h"

namespace counterstrikesharp {
namespace timers {
double universal_time = 0.0f;
double timer_next_think = 0.0f;
} // namespace timers

timers::Timer::Timer(float interval, float exec_time, CallbackT callback, int flags)
    : m_interval(interval), m_exec_time(exec_time), m_flags(flags), m_kill_me(false), m_in_exec(false)
{
    m_callback = globals::callbackManager.CreateCallback("Timer");
    m_callback->AddListener(callback);
}

timers::Timer::~Timer() { globals::callbackManager.ReleaseCallback(m_callback); }

TimerSystem::TimerSystem()
{
    m_has_map_ticked = false;
    m_has_map_simulated = false;
    m_last_ticked_time = 0.0f;
}

void TimerSystem::OnAllInitialized() { m_on_tick_callback_ = globals::callbackManager.CreateCallback("OnTick"); }

void TimerSystem::OnShutdown() {
    for (const auto& timer : m_once_off_timers) timer->m_kill_me = true;
    for (const auto& timer : m_repeat_timers) timer->m_kill_me = true;
    m_once_off_timers.clear();
    m_repeat_timers.clear();
    globals::callbackManager.ReleaseCallback(m_on_tick_callback_);
    m_on_tick_callback_ = nullptr;
}

void TimerSystem::OnLevelEnd()
{
    globals::timerSystem.RemoveMapChangeTimers();

    m_has_map_simulated = false;
    m_has_map_ticked = false;
}

void TimerSystem::OnStartupServer(bool levelShutdown)
{
    if (levelShutdown && m_has_map_ticked)
    {
        CALL_GLOBAL_LISTENER(OnLevelEnd());

        CSSHARP_CORE_TRACE("name={0}", "LevelShutdown");
    }

    // Reset is UNCONDITIONAL -- universal_time math in OnGameFrame depends on
    // m_has_map_ticked being false on the first frame of every new session,
    // whether that session came in via a genuine LevelShutdown or a workshop
    // ss_dead reload.
    m_has_map_ticked = false;
    m_has_map_simulated = false;
}

void TimerSystem::OnGameFrame(bool simulating)
{
    if (simulating && m_has_map_ticked)
    {
        timers::universal_time += globals::getGlobalVars()->curtime - m_last_ticked_time;
        if (!m_has_map_simulated)
        {
            m_has_map_simulated = true;
        }
    }
    else
    {
        timers::universal_time += globals::engine_fixed_tick_interval;
    }

    m_last_ticked_time = globals::getGlobalVars()->curtime;
    m_has_map_ticked = true;

    RunFrame();

    if (m_on_tick_callback_->GetFunctionCount())
    {
        m_on_tick_callback_->ScriptContext().Reset();
        m_on_tick_callback_->Execute();
    }

    globals::playerManager.RunAuthChecks();
}

double TimerSystem::CalculateNextThink(double last_think_time, float interval)
{
    if (timers::universal_time - last_think_time - interval <= globals::engine_fixed_tick_interval)
    {
        return last_think_time + interval;
    }
    return timers::universal_time + interval;
}

void TimerSystem::RunFrame()
{
    if (m_running_frame) return;
    m_running_frame = true;
    struct FrameGuard { bool& running; ~FrameGuard() { running = false; } } guard{m_running_frame};
    // Snapshots retain timers deleted by another callback. Newly created timers
    // start next pass, and recursive RunFrame cannot execute an active timer twice.
    const auto once = m_once_off_timers;
    const auto repeat = m_repeat_timers;
    for (auto it = once.rbegin(); it != once.rend(); ++it)
    {
        const auto& timer = *it;
        if (timer->m_kill_me || timer->m_in_exec || timers::universal_time < timer->m_exec_time) continue;
        timer->m_in_exec = true;
        timer->m_callback->ScriptContext().Reset();
        timer->m_callback->Execute();
        timer->m_in_exec = false;
        timer->m_kill_me = true;
        std::erase(m_once_off_timers, timer);
    }
    for (auto it = repeat.rbegin(); it != repeat.rend(); ++it)
    {
        const auto& timer = *it;
        if (timer->m_kill_me || timer->m_in_exec || timers::universal_time < timer->m_exec_time) continue;
        timer->m_in_exec = true;
        timer->m_callback->ScriptContext().Reset();
        timer->m_callback->Execute();
        timer->m_in_exec = false;
        if (!timer->m_kill_me) timer->m_exec_time = CalculateNextThink(timer->m_exec_time, timer->m_interval);
    }
}

void TimerSystem::RemoveMapChangeTimers()
{
    auto remove = [](const auto& timer) {
        if (!(timer->m_flags & TIMER_FLAG_NO_MAPCHANGE)) return false;
        timer->m_kill_me = true;
        return true;
    };
    std::erase_if(m_once_off_timers, remove);
    std::erase_if(m_repeat_timers, remove);
}

timers::Timer* TimerSystem::CreateTimer(float interval, CallbackT callback, int flags)
{
    float exec_time = timers::universal_time + interval;

    auto timer = std::make_shared<timers::Timer>(interval, exec_time, callback, flags);

    if (flags & TIMER_FLAG_REPEAT)
    {
        m_repeat_timers.push_back(timer);
        return timer.get();
    }

    m_once_off_timers.push_back(timer);
    return timer.get();
}

void TimerSystem::KillTimer(timers::Timer* timer)
{
    if (!timer) return;
    auto remove = [timer](const auto& owned) {
        if (owned.get() != timer) return false;
        owned->m_kill_me = true;
        return true;
    };
    std::erase_if(m_once_off_timers, remove);
    std::erase_if(m_repeat_timers, remove);
}

double TimerSystem::GetTickedTime() { return timers::universal_time; }
} // namespace counterstrikesharp
