# WheelWizard Native developer MVP

An Apple Silicon SwiftUI frontend with one bundled C# helper per session. Game executables and data stay outside the frontend bundle. This is a local developer build, ad-hoc signed, with no distribution signing or notarization.

## Build and run

Prerequisites: Apple Silicon macOS 26+, selected Xcode with its command-line tools, .NET 10 SDK plus the SDK required by your WiiCompiled checkout, CMake and Ninja (normally Homebrew), an existing WiiCompiled checkout, and a clean PAL RMCP01 WBFS. Toolchain installation is outside this MVP. The build script uses the selected checkout's translator and native toolchain and modifies its generated caches and extracted `Assets` directory.

From the WheelWizard repository:

```sh
./macos/Native/build.sh /absolute/path/to/WiiCompiled
open macos/Native/artifacts/WheelWizardNative.app
```

The command builds with Xcode, publishes the helper and translator as self-contained `osx-arm64` applications with trimming and NativeAOT disabled, downloads nodtool `v2.0.0-alpha.10` for macOS ARM64, verifies its pinned SHA-256, bundles the tools, and verifies the app's ad-hoc signature. Re-run it when the selected translator source changes. The packaged helper and translator do not need a system .NET runtime. The frontend and helper contain no Avalonia dependencies.

Choose **Setup**, select the WBFS and WiiCompiled checkout, then **Check Prerequisites**. Advanced tool paths are editable. Select Mario Kart Wii and Build, or select Retro Rewind, Download and Install RR, then Build. RR always uses the offline mode. Play requires a published app and a matching receipt. Use **Rebuild** after local uncommitted source edits; the receipt tracks the workspace commit, not uncommitted edits.

Settings exposes volume (0–1) and resolution (1×, 1.5×, 2×, 3×). Settings writes, install, and build are disabled during a game. Settings reload after it exits, including changes made with F10. Cancel / Stop stops the helper's owned process tree. Quitting during an operation requires choosing Keep Running or Stop and Quit.

## Isolated data

Everything managed by this frontend lives under `~/Library/Application Support/WheelWizardNative`:

| Path | Contents |
| --- | --- |
| `preferences.json` | Selected input and tool paths |
| `RetroRewind/RetroRewind6` and `RetroRewind/riivolution` | Fresh package, including its version and XML |
| `Staging/<unique-operation>` | Downloads, extraction, build output and temporary files |
| `Runtime/WiiCompiled.app`, `Runtime/RetroRewind.app` | Published products |
| `Runtime/base.json`, `Runtime/retro-rewind.json` | Product receipts with WBFS SHA-256, RR Code.pul SHA-256, checkout path/revision and build mode |
| `Runtime/portable.txt`, `Runtime/UserData` | Shared managed runtime configuration, NAND, saves, caches and game logs |
| `Logs/*.jsonl` | Full helper operation logs; the frontend retains only 500 lines |

Each launch writes `paths.dvd_root` to the selected checkout's `Assets/DATA`, clears `overlay_roots`, and selects an empty RR root for vanilla or the managed `RetroRewind6` for RR. Networking is disabled. Launch passes no game arguments. Publication moves apps, receipts, portable marker and configuration only after successful compilation and output validation; failures roll back. An exceptional rollback failure leaves a `recovery.required` marker and preserves the staging backup for recovery.

The fresh installer refuses an existing managed RR directory, including an incomplete one. No migration, adoption of existing saves, Dolphin integration, incremental RR update, or toolchain installation is implemented. A session lock prevents multiple helpers from changing the same managed root. `WHEELWIZARD_NATIVE_ROOT` is a helper-only override for isolated test fixtures.

## Shared services and protocol

`WheelWizard.Core` contains `RetroRewindPackage` (URL/version parsing, download, safe extraction, package validation) and `RuntimeConfiguration` (file access, conversions and preservation of unrelated TOML lines). Avalonia's fresh install and archive extraction call this package service; its settings manager and setting value adapter use this configuration service. UI dialogs, save migration, publication into Avalonia's directories and incremental-update orchestration remain in Avalonia.

`WheelWizard.Host` owns the native workflow and process lifetime. Protocol version 1 is UTF-8 newline-delimited JSON on stdin/stdout; diagnostics use stderr. Every request requires a unique `id`. Only one operation is accepted at a time; `cancel` remains available. EOF or SIGTERM cancels owned work. There is no HTTP listener.

Example request (one line):

```json
{"version":1,"id":"example-1","command":"build","product":"retro-rewind","setup":{"wbfs":"/path/Game.wbfs","workspace":"/path/WiiCompiled","cmake":"/opt/homebrew/bin/cmake","ninja":"/opt/homebrew/bin/ninja","nodtool":"/path/nodtool","translator":"/path/Translator.Cli"}}
```

Commands: `preflight`, `status`, `package-status`, `package-latest`, `install`, `build`, `config-read`, `config-write`, `launch`, `cancel`. Products are `base` and `retro-rewind`. `config-write` accepts `settings: {"volume":0.5,"resolutionMultiplier":1.5}`. Events carry `version`, `id`, `kind`, `data`, `outcome`, and `error`. Kinds are `progress`, `log`, `status`, and terminal `result`; terminal outcomes are `success`, `cancelled`, or `failure`. Progress has a stage and optional percentage; build stages forward `MKWCBUILD:STEP:` markers. Local status/launch makes no network request. Latest package status is a separate optional network command.

## Automated verification

```sh
dotnet test WheelWizard.Core.Test -c Release -m:1 -p:CSharpier_Bypass=true
dotnet test WheelWizard.Test -c Release -p:DefineConstants=MACOS -m:1 -p:CSharpier_Bypass=true
python3 macos/Native/test_bridge.py
```

The CSharpier bypass prevents builds from changing unrelated source line endings. The bridge tests use the real bundled self-contained helper, temporary fixtures, fake child executables, and a nonexistent `DOTNET_ROOT`. They test protocol errors/recovery, busy rejection, cancellation, failed readiness and SIGTERM cleanup. Core/workflow tests cover settings, archives, HTTP failure/cancellation, output draining, argument boundaries, real filesystem publication rollback, successful-exit/missing-output failure, product selection and input changes.

For real build and launch verification through the same protocol:

```sh
python3 macos/Native/demo.py /path/WiiCompiled '/path/Game.wbfs' preflight install build:base build:retro-rewind status
python3 macos/Native/demo.py /path/WiiCompiled '/path/Game.wbfs' launch:base launch:retro-rewind --stop-after 30
```

Omit `install` if the managed RR package is already installed. `--stop-after` is a startup probe using cancellation; it does **not** establish normal exit or race acceptance. Without it, close the game normally to continue. Operation logs include exact runtime paths and build stage output. See [ACCEPTANCE.md](ACCEPTANCE.md) for actual results and remaining manual checks.
