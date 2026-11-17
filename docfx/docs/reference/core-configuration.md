---
title: Core Configuration
description: Summary for core configuration values
---

# Core Configuration

Summary for core configuration values

## PublicChatTrigger

List of characters to use for public chat triggers.

## SilentChatTrigger

List of characters to use for silent chat triggers.

## FollowCS2ServerGuidelines

Per [CS2 Server Guidelines](https://blog.counter-strike.net/index.php/server_guidelines/), certain plugin
functionality will trigger all of the game server owner's Game Server Login Tokens
(GSLTs) to get banned when executed on a Counter-Strike 2 game server.

Enabling this option will block plugins from using functionality that is known to cause this.
This option only has any effect on CS2. Note that this does NOT guarantee that you cannot
receive a ban.

> [!NOTE]
> Disable this option at your own risk.

## PluginHotReloadEnabled

When enabled, plugins are automatically reloaded when their .dll file is updated.

## PluginAutoLoadEnabled

When enabled, plugins are automatically loaded from the plugins directory on server start.

## ServerLanguage

Configures the default language to use for server commands & messages. The format for the culture name based on RFC 4646 is `languagecode2-country`/`regioncode2`, where `languagecode2` is the two-letter language code and `country/regioncode2` is the two-letter subculture code. Examples include `ja-JP` for Japanese (Japan) and `en-US` for English (United States). Defaults to "en".

## UnlockConCommands

When enabled, will remove the `FCVAR_HIDDEN`,`FCVAR_DEVELOPMENTONLY`, `FCVAR_MISSING0`, `FCVAR_MISSING1`, `FCVAR_MISSING2`, `FCVAR_MISSING3` flags from all console commands.

## UnlockConVars

When enabled, will remove the `FCVAR_HIDDEN`,`FCVAR_DEVELOPMENTONLY`, `FCVAR_MISSING0`, `FCVAR_MISSING1`, `FCVAR_MISSING2`, `FCVAR_MISSING3` flags from all console variables.

## AutoUpdateEnabled

When enabled, CS# will check for any updates to the gamedata.json file and automatically update it if a new version is available.

## AutoUpdateURL
The URL to use for the auto-update feature. This URL should point to a JSON file that contains the latest version of the gamedata.json file.

## MaximumFrameTasksExecutedPerTick
The maximum number of `NextFrame`, `NextWorldUpdate` and due `RunOnTick` tasks executed per dispatch. Each queue has its own limit. Defaults to 1024; values below 1 are treated as 1. This is a count limit, not a time budget: a running callback cannot be preempted.

Pending tasks are canceled on plugin unload and map end. Their async results become canceled, so code awaiting them should handle cancellation. Tasks already executing are allowed to finish. Tasks queued during a dispatch run in a later pass. `RunOnTick` executes due work in tick order (FIFO for equal ticks), with overflow deferred to subsequent ticks. Workshop session restarts also cancel old tick-indexed tasks.

`NextFrame` and `RunOnTick` require game ticks; `NextWorldUpdate` continues during hibernation. Plugin unload cleanup that must run should be performed synchronously in `Unload`, rather than queued from the unloading plugin.

## Optional runtime diagnostics (Katujemy build)

Diagnostics are disabled by default. To enable them, add these fields to your existing `configs/core.json`, preserving the other settings, then run `css_core_reload`:

```json
"RuntimeDiagnosticsEnabled": true,
"SlowCallbackMilliseconds": 5,
"DiagnosticsIntervalSeconds": 30,
"DiagnosticsQueueThreshold": 256
```

At most one summary is logged per interval (minimum 5 seconds), and only if there were slow callbacks or the pending task count reached the threshold. The summary includes queue counts and the top eight slow callback labels with count, total and maximum time. Up to 128 labels are retained per interval; excess samples are counted, not stored. Labels contain assembly/method names, not plugin objects.

Measurements are wall time, include nested calls and must not be summed as exclusive CPU time. Native wrapper callbacks and their timer bodies can appear separately. These diagnostics observe scheduling and slow handlers; they do not automatically kill plugins or change server settings. Disable with `"RuntimeDiagnosticsEnabled": false` and `css_core_reload`.
