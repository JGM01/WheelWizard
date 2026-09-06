# Native MVP acceptance record

Verified on Apple Silicon macOS on 2026-09-05, using Xcode 27.0 beta and the supplied PAL WBFS. These are real package, translation, compilation and runtime results; fixture tests are listed separately.

## Real products

| Check | Result |
| --- | --- |
| Fresh RR download, extraction, validation and managed installation | Passed, package **6.12.6** |
| Vanilla build with `--profile base` | Passed; published `Runtime/WiiCompiled.app` |
| RR build with `--profile both --skip-retro-wfc-payload` | Passed; published both apps |
| Vanilla startup | Passed; empty RR root, no overlay roots, managed DVD and user-data paths |
| RR startup | Passed; managed `RetroRewind6` selected; network disabled |
| Change volume to 0.4 and resolution multiplier to 1.5 | Passed; both real runtime startup logs report the new values |
| Normal macOS Quit and helper result | Passed for both; exit code 0, configuration reloaded, both products Ready |
| Cancellation during startup | Passed for both; terminal outcome `cancelled`, local readiness retained |
| Native app build, bundled helper and translator | Passed; self-contained ARM64, no Avalonia dependencies |
| Native frontend startup | Passed; native window created, preflight has no errors, helper reports both products Ready and loads saved settings |
| Manual race, audible/visual setting changes, F10 round trip | **Pending manual acceptance** |

Recorded build inputs:

- WiiCompiled revision: `56db6ba641739472e1939abf571e013b176b272b`
- WBFS SHA-256: `A22C819C5B8321E4E77C2B9AFB1C08B9DA31298627676B5C94A3F4C0AE409C69`
- RR Code.pul SHA-256: `3144837FE705B3E70D3B3C626AA16E8A51FF37C9498377F79BBE1F83B3461424`
- RR receipt mode: `offline`

No translation blocker was encountered. Startup logs alone do not prove that a race works or that audio and rendering are correct. Screen capture and visual UI inspection were not performed; those checks remain manual.

Full operation logs remain in `~/Library/Application Support/WheelWizardNative/Logs`. Use **Reveal Logs** in the app. Initial build/startup evidence includes:

- Vanilla build: `20260905-*.jsonl`, request `64fecaa0-da7e-4348-a1ea-1809d5be9a0e`
- Offline RR build: request `a66f624c-bd4d-49f4-a822-da6cea3e7b78`
- Initial RR startup: `20260905-160047-31c0bde29dd94fc4904757f86124d1eb.jsonl`
- Normal exit verification: requests `b890bb2c-3a53-457d-9ad6-7783d10a928b` (vanilla) and `e474d3ca-32dd-43b4-b284-11e0737c5cf9` (RR)

The operation log's first event identifies its command and file. Each launch logs its product and config location. Runtime logs remain under `Runtime/UserData/Logs`. Temporary orchestration transcripts from this implementation session are `/tmp/ww-real-demo.jsonl`, `/tmp/ww-real-rr.jsonl`, `/tmp/ww-rr-startup.jsonl`, `/tmp/ww-settings-startup.jsonl`, and `/tmp/ww-normal-exit.jsonl`; durable operation logs are the primary evidence.

## Manual checklist

- [ ] In the native app, select **Mario Kart Wii**, press Play, and reach a race. Confirm vanilla tracks/UI and usable controls.
- [ ] Close the game normally. Confirm the native UI returns to Ready and enables Build, Play and Settings.
- [ ] Select **Retro Rewind** (Offline build), press Play, and reach a race on an RR track. Do not use online play for this slice.
- [ ] Close normally and confirm Ready again.
- [ ] Set a noticeably different volume and resolution in native Settings, save, and launch each product. Confirm audible volume and visual resolution changes in a race.
- [ ] Change a setting with F10, exit, and confirm the native Settings window reloads that value.
- [ ] While a game is running, confirm settings writes/build/install are disabled; exercise Keep Running and Stop and Quit from the quit confirmation.

Record any failure with the selected product, receipt, operation log, runtime log, and the reproduction command below. Leave failed or unperformed items unchecked.

```sh
python3 macos/Native/demo.py /path/to/WiiCompiled '/path/to/Game.wbfs' launch:base
python3 macos/Native/demo.py /path/to/WiiCompiled '/path/to/Game.wbfs' launch:retro-rewind
```

## Automated checks

**29 core/workflow tests, 210 Avalonia tests, and 3 bundled-helper bridge tests passed.**

Core/workflow tests cover package parsing, archive traversal, download failure/cancellation, malformed configuration preservation, conversion, process arguments/output/cancellation, publication rollback and product identity. The existing Avalonia suite passes with shared-service adapter regression tests. The bundled-helper bridge suite passes all three tests, including malformed protocol recovery, self-contained execution, busy rejection, cancellation and SIGTERM cleanup. Reproduce with the commands in [README.md](README.md).

## Mod library and preview extraction (2026-09-06)

**97 Core tests, 204 framework tests, and 5 bundled-helper bridge tests passed.** The native app built and passed ad-hoc signature verification; the Windows framework configuration also built. ZIP and 7z import fixtures were exercised; RAR remains supported by the existing SharpCompress reader but was not independently fixture-tested in this batch.

Automated verification covers metadata/defaults, adapter notifications, staged import and cleanup, collision precedence, tagged archives, file copying, and helper library/preview commands. The bundled-helper tests exercise import, enabled state, ordering, removal, duplicate/invalid requests, traversal rejection, cancellation cleanup, busy rejection, and persistence across helper restarts. Native launch code does not apply the managed mod library.

Manual UI acceptance remains to be recorded:

- [ ] Import an archive through the native name sheet; confirm the new mod appears.
- [ ] Toggle enabled state and move mods up/down; restart and verify saved state.
- [ ] Preview overlapping files; inspect winner/overwritten sources and the All Files toggle.
- [ ] Confirm library edits invalidate the preview; exercise empty/no-conflict/error states.
- [ ] Cancel and confirm removal in separate attempts; confirm the source archive remains.
- [ ] Confirm the native Play limitation is visible and existing game launch behavior is unchanged.
