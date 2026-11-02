# MoonSharp Lua plugin spike (not product integration)

This is an isolated experiment. `MoonSharp` is referenced only by `spikes/LuaPluginRuntime`; the main application and its plugin UI do not reference or expose the runtime. Do not connect this project to `MdExplorer.UI`, add plugin installation, or permit scripts to perform file operations until the gates below are reviewed and explicitly approved.

## What's in the spike

- `spikes/LuaPluginRuntime`: small net10.0 library using MoonSharp 2.0.0 and `CoreModules.Preset_HardSandbox`.
- `spikes/LuaPluginRuntime/Samples`: three embedded, editable Lua demo scripts (image organization, rename preview, inventory report).
- `spikes/LuaPluginRuntime.Lab`: desktop runner; `dotnet run --project spikes/LuaPluginRuntime.Lab -- organize-images 3` prints a dry-run preview.
- `spikes/LuaPluginRuntime.Android`: separate native Android host app, not the Eidoria app. It invokes the same demo at startup and reports success/failure in Android logcat.
- `tests/MdExplorer.LuaRuntime.Tests`: tests for demo output, disabled `os`/`io`, rejected actions, input bounds, and a loop instruction budget.

The only Lua globals are an integer file count and `eidoria_preview(title, action, target)`. The callback accepts only three strings and one of `copy-preview`, `rename-preview`, or `report-only`; it returns preview text and performs no I/O. Results state that host confirmation is required and files were not written. The script gets no host object, path, filesystem API, CLR interop, or notification API.

## Running locally

Requires .NET SDK 10.0.100 (see `global.json`). This sandbox currently has no `dotnet` executable, so these commands have not been run locally.

```sh
dotnet test tests/MdExplorer.LuaRuntime.Tests -c Release
dotnet run --project spikes/LuaPluginRuntime.Lab -- organize-images 3

dotnet publish spikes/LuaPluginRuntime.Lab -c Release -r linux-x64 \
  --self-contained true -p:PublishAot=true -o artifacts/lua-linux-x64
./artifacts/lua-linux-x64/MdExplorer.LuaPluginRuntime.Lab
```

For Android, install the .NET Android workload, publish the separate probe with trimming and AOT compilation, install its APK on an API 35 emulator/device, and verify the `LuaRuntimeSpike` `PASS:` log entry:

```sh
dotnet workload install android
dotnet publish spikes/LuaPluginRuntime.Android -c Release \
  -p:PublishTrimmed=true -p:TrimMode=full -p:RunAOTCompilation=true \
  -o artifacts/lua-android
```

CI has an informational Windows/Linux Native AOT job and an Android trimmed/AOT publish plus emulator launch. These jobs are deliberately non-blocking while the spike is being evaluated; a green build does not by itself mean the integration gates are approved.

## Go/no-go gates before product integration

1. **Runtime and maintenance:** verify the exact NuGet package identity, source, checksum, BSD-3-Clause license text/attribution, and transitive dependency licenses. MoonSharp 2.0.0 is old; review upstream maintenance/security history and known advisories. Do not silently switch to a fork or accept copyleft dependencies.
2. **Native AOT + trimming:** run tests and publish/execute the desktop probe for every supported shipping RID (at minimum `win-x64`, `linux-x64`, plus any arm64 targets we ship) with trimming/AOT warnings reviewed. No ignored linker/AOT warnings without an explicit justification and regression test. Verify all three embedded samples survive trimming.
3. **Android:** build the probe with full trimming and Android AOT, launch it on an emulator, then test on the actual supported Android ABIs/devices (especially arm64-v8a). Confirm resource loading, hard-sandbox execution, and repeatable results. Emulator x86_64 success alone is not sufficient.
4. **Security:** hard sandbox is a language-level boundary, not an OS process sandbox. Keep `io`, `os`, `debug`, dynamic loading, and CLR interop unavailable; expose only primitive data and a narrow, validated host API. Expand adversarial tests for infinite loops, excessive source/input sizes, malformed return types, and host callback abuse. Current instruction quotas do not impose a hard heap-memory quota; do not run arbitrary untrusted marketplace plugins in-process.
5. **Safe effects:** Lua may eventually construct a typed, bounded Operation Plan only. The existing application must validate and show that plan, require explicit user confirmation, then route every filesystem write through the existing task queue and its path/symlink protections. Never give a script direct filesystem access, arbitrary path writes, loops that dispatch unbounded work, or a bypass to the task queue.
6. **Decision:** record test results and residual risks, then ask whether to proceed. Until the user explicitly approves after review, keep the runtime isolated from the application and do not add a production plugin UI.

## Current status

Spike scaffolding is present, but no local .NET build/test, AOT publish, Android install/launch, license audit, or security review has completed. Treat all platform support as **unverified**. The CI jobs are expected to provide the first executable evidence when run.
