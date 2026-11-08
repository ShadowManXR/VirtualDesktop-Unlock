# Per-game FPS and resolution presets

Research and implementation record: 2026-11-08. The investigation progressed to an installed launch helper and selected-game preset editor, as recorded below.

## Conclusion

An automatic **144 FPS / Low** launch preset for Forefront is now implemented, installed locally, and verified through VD's headset Games tab on the supported Streamer 1.34.25.0 / Quest 3 / VDXR setup. The hook changed settings before the game process started, the user confirmed Low / target 144 without manual changes, and VDXR logged the Low-quality render recommendation. The selected-game editor also saves profiles for other Steam identities. A protocol-level headset acknowledgement remains outside this implementation.

**Live update:** Switching VD's ordinary FPS and graphics-quality selections from the PC **is confirmed**: the headset displayed 90 FPS / Medium after the helper requested it, and returned to 144 FPS / High after restoration. See the completed live tests below.

**Forefront follow-up:** A controlled pre-launch 144 FPS / Low selection was subsequently confirmed on the headset before Forefront started. VDXR reported a lower recommended per-eye resolution, and the user reported the in-game VD overlay showed target 144 with actual FPS near 144. That manual test was followed by the automatic launch implementation and live test recorded below.

If “Virtual Desktop cannot be modified” means leaving its executable and headset APK unchanged, extending the existing in-process helper is a plausible approach. It would still intercept and change Streamer behavior in memory, with compatibility tied to a verified binary. If the constraint excludes all in-process interception as well, the inspected system does not establish a general external solution that can delay every native VD Games-tab launch. A separate VD Unlock launcher could control launches it initiates, but would not meet the same Games-tab requirement.

Assumption: FPS means the VR streaming/refresh-rate target, and resolution means VR render resolution. Neither is a guarantee of achieved game performance. A game's internal FPS limiter, its desktop window resolution, encoded video dimensions, and the headset panel resolution are distinct controls.

## Evidence from this repository and installed Streamer

The installed Streamer is 1.34.25.0, SHA-256 `0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8`, matching the existing library helper's supported binary.

- `SteamVRLibrary/Program.cs`, `LaunchSelected()`, currently sends `steam://rungameid/{id}`. It does not intercept headset launches or wait for settings.
- `src/RuntimeOverrides/LibraryOverrides.cs` loads a helper into Streamer with an AppDomain manager and alters its game-discovery dictionary. It does not currently gate launches.
- `src/RuntimeOverrides/BitrateRuntime.cs` demonstrates version/hash-guarded runtime interception infrastructure, but its bitrate hook supports 1.34.23.0 only. This is architectural precedent, not a reusable verified launch hook for 1.34.25.0.
- Fresh read-only IL inspection of 1.34.25.0 identifies a candidate `GameManager` entry at token `060001E8`, with signature `static Task(GameID)`. Its asynchronous launch machinery leads to game-descriptor processing and process/service launch paths. A prototype must verify which headset requests reach this entry and whether an earlier boundary is needed to precede runtime startup.
- The downstream descriptor launcher is `060001F9`. It performs runtime selection/preparation and invokes multiple launch routes. Hooking a generic `Process.Start` would miss some of those routes or act too late.
- The `GameManager` `EventHandler<GameID>` notification is invoked through `06000200`, including after process handling and on exit. Its presence is not evidence of a cancellable before-launch event.

Proprietary IL and the isolated probe are retained in ignored `artifacts/preset-research/`; they are not public source deliverables.

## Settings paths

| Control | Local evidence | Remaining question |
| --- | --- | --- |
| VR FPS target | `SharedUserSettings.VRFramerate` setter (`060213A3`) updates shared settings and raises notifications. `SharedMobileSettings.VRFramerate` supplies the PC effective value. | Does a connected headset receive/apply an ordinary supported rate, and when do SteamVR/VDXR timing and video delivery settle? |
| VD graphics quality | `SharedUserSettings.VRGraphicsQuality` setter (`060213A5`) updates shared settings. `OculusStreamer` subscribes to quality changes and forwards quality into native VR configuration. | Does changing it before the launch gate releases update the runtime's render recommendation and encoder configuration consistently? |
| Additional render override | `StreamerSettings.RenderResolutionOverride` getter/setter (`060003C5`/`060003C6`) exists. Setter IL normalizes near 1 and clamps to 0.5–2.0. `OculusStreamer` reads it during native configuration and handles its change event. | Native interpretation, effect on each runtime, persistence/restoration, and effective pixel dimensions require validation. Do not label it as a particular pixel resolution or percentage convention yet. |

An isolated .NET Framework probe passed **15 checks**: five ordinary Quest 3 FPS setter/effective-getter cases (72, 80, 90, 120, 144), nine defined graphics-quality setter/getter cases, and the candidate launch signature. These verify PC object behavior only; they do not establish which options the headset exposes or accepts. The launch method was never invoked.

The first attempt to probe the render override on an uninitialized settings object encountered a null save-timer dependency. That test is not counted as passing. Any resulting settings serialization was redirected to `artifacts/preset-research/isolated-settings`; live preferences were not used. The render-override conclusions above come from IL inspection, not a completed setter test or live rendering test.

The prior live experiments in [fps-research.md](fps-research.md) are a significant caution: setting 180/200 on the PC did not change the measured 72 Hz headset mode or roughly 72 FPS delivery. Those experiments do not establish failure of ordinary 72/90/120 changes, but they do rule out treating PC setter success as confirmation. Extended FPS unlocks should remain a separate experiment.

## Why a process watcher is insufficient

Microsoft documents `Win32_ProcessStartTrace` as notification that a process **has started**. It cannot hold the original launch pending preset application. Polling or ETW may help track exit, diagnose launches, or provide best-effort detection, but cannot guarantee pre-initialization application. Suspending a newly observed process also leaves a race and does not account for runtime work VD performed before creating it.

VR applications obtain recommended render dimensions from their runtime and create rendering resources. OpenXR exposes recommended dimensions through `XrViewConfigurationView`; the application selects its swapchain dimensions. OpenVR exposes `GetRecommendedRenderTargetSize`. Therefore, changing settings after resource creation cannot be assumed to resize every game. Holding launch before runtime/game initialization is the appropriate target; some transitions may still require a runtime restart.

## Proposed implementation

1. Store presets in VD Unlock, keyed by platform and stable game ID (initial scope: Steam app ID), with optional headset/runtime constraints. Start with a supported VR FPS target and VD quality tier; defer arbitrary pixels and extended FPS values.
2. Extend the helper with a separately verified launch interceptor, guarded by version, binary hash, method signature, and expected call path. Preserve the original launcher and its runtime selection. Unknown builds retain ordinary VD launching and report profiles unavailable.
3. On a VD launch request, serialize profile transactions, resolve the game, capture original settings in a recovery record, and apply the preset through normal setters on the correct dispatcher. Do this before calling the original launch path, not in a notification after it starts. Avoid blocking the UI/network dispatcher while awaiting completion.
4. Wait for bounded evidence that the requested configuration is effective. PC property readback is insufficient; validate headset refresh rate plus runtime render size/timing and stream readiness. A fixed sleep is not confirmation. If no reliable acknowledgement exists, the feature must report that limitation and cannot claim guaranteed application before launch.
5. Release the original launch once ready. Handle double clicks, launch cancellation, failures, disconnected headsets, and games already running. An explicit per-profile policy should determine whether failed application cancels launch or launches with current settings, with the result visible in status/logs.
6. Track the actual game/session, accounting for Steam and launcher child processes. Restore captured values on exit/failure and recover after a crash. Preserve subsequent manual user changes rather than unconditionally overwriting them. These settings are shared session state, so overlapping games need a defined single-owner policy.
7. Route VD Unlock's own launch action through an equivalent verified preparation sequence; changing only that button does not cover the headset Games tab.

An OpenXR API layer could offer a separate per-application render-resolution mechanism for compatible OpenXR games, but does not automatically cover OpenVR/Oculus routes, control VD's headset refresh rate, or intercept VD before it launches a process. SteamVR per-application resolution is likewise a runtime-specific alternative, not a complete VD preset implementation.

## Minimum proof before building the UI

Instrument a temporary launch gate first. Confirm the requested app ID arrives before any relevant runtime/process launch; compare ordering on SteamVR and VDXR routes. Then test two ordinary supported FPS targets and two quality tiers with the headset connected. Record headset actual Hz, runtime frame timing/recommended size, actual game render dimensions when available, and stream frame delivery independently.

Repeat game A → exit → game B, failed launch, quick repeated clicks, manual setting changes, reconnect, helper crash/recovery, and an already-running SteamVR session. Establish when a runtime restart is necessary. Only after this works should the production profile editor and installer changes be added. No live settings, installed helper, Streamer executable, or headset APK were changed for this investigation.

## Follow-up: ordinary VD preset changes, 2026-11-08

The narrower first milestone is changing VD's existing VR FPS and graphics-quality selections from the PC, without intercepting a game launch or adding arbitrary render dimensions.

Additional client inspection shows that incoming property messages call `JsonHelper.PopulateMember`, which populates the settings object through its serializer. The setters for both `VRFramerate` and `VRGraphicsQuality` raise their respective change events, and the Streaming tab subscribes to both. The client also compares selected FPS/quality with its active values to display a game-restart warning while VR is active. This supports testing changes before a game initializes; it does not prove every change becomes effective immediately in an existing session.

Six new isolated integration checks passed using the installed PC binary's actual member-message serializer and receiver: FPS 90/120/72 and quality Medium/Ultra/High. Each serialized payload updated the property and raised exactly one matching settings-change event. This is stronger than ordinary JSON round-tripping but still does not test live transport or execute the Android receiver.

A temporary diagnostic helper was built in ignored `artifacts/preset-research/`. It retains existing library/bitrate behavior, remains observation-only without an explicit command, accepts only ordinary Quest 3 FPS values and defined quality values, and limits commands to three minutes. It captures the original selections before changes, restores on expiry or command removal, and preserves subsequent manual selections independently for each property. Thirteen control checks passed, along with a startup-helper loading and isolated library-override smoke test. It records PC selection/effective FPS, quality, connection/population state, client-reported refresh and VR-session state; none of these alone is treated as headset receipt confirmation.

At preflight, ADB found the Quest 3 with VD 1.34.25.0 installed, but its VD process was not running. The temporary helper was **not installed**, Streamer was **not restarted**, and no live preset was changed. The user was asked to open VD and connect to this PC for the live test. A brief Streamer restart and another reconnect will be necessary to load the diagnostic helper.

The planned live validation was to first compare the headset's displayed selections before/during/after a short supported preset change, then compare actual VR-session refresh and render dimensions across a game restart. A desktop-mode display refresh is not a measurement of the VR FPS selection: `Scene.AdjustPerformanceSettings` uses separate desktop and VR settings according to streaming mode. An ADB check found no accessible settings at the guessed external files path, so no client settings acknowledgement was obtained that way. Live preset-selection confirmation was subsequently obtained from the user, as recorded below.

### Live preset selection test completed

The user subsequently connected VD and opened its Streaming settings. The temporary helper was installed with a verified backup, using the normal Windows administrator prompt, and Streamer was restarted. A first unelevated copy attempt was denied; the original DLL remained unchanged and the original Streamer was relaunched before the successful elevated installation.

Baseline was **144 FPS / High** (`VRFramerate=144`, `VRGraphicsQuality=2`). A two-minute lease requested **90 FPS / Medium** (`90`, `1`) through the normal shared-settings setters. PC selected/effective FPS changed to 90 and quality to 1, with initialized settings, an active connection, and `_populating=False`. Crucially, the user independently confirmed that the headset Streaming settings displayed **90 FPS and Medium**. This provides live headset receipt/application evidence for both preset selections, beyond PC property readback.

The command was removed early. The helper restored **144 FPS / High**, with no pending recovery record. The user independently confirmed those original selections had returned on the headset. Thus PC-driven switching and restoration of both ordinary VD preset selections worked without modifying Streamer's executable or the headset APK.

This test ran outside an active VR game (`HasVR=False`). The Quest's ADB/USB connection was unavailable during the live test. No actual headset Hz, game render dimensions, encoder dimensions, sustained FPS, or timing relative to game launch was measured. The result establishes remote preset selection, not guaranteed immediate reconfiguration of a running game or a working per-game launch interceptor. Extended FPS values and arbitrary render resolutions remain unverified.

Diagnostic evidence is retained in ignored `artifacts/preset-research/`: `live-baseline.xml`, `live-90-medium.xml`, `live-restored.xml`, the installation receipt, and the restoration receipt. Production source and UI were not changed.

After testing, the original helper and installation record were restored from the verified backup. The restored helper SHA-256 is `179FC5B9C756C669ADCEBC791A46300B64F7448A5C81FC819252C0001FE64D5B`; Streamer restarted with its executable signature valid and all 19 current custom game overrides logged. No experiment command or recovery file remained. No temporary diagnostic helper remains installed.

### Forefront: applying 144 FPS / Low before launch

The user requested a before-launch test using Forefront. The same verified diagnostic helper was reinstalled temporarily, with another backup and rollback. After reconnecting, the baseline was **90 FPS / High**, rather than the prior session's 144 FPS / High. Recovery captured the current 90 / High baseline.

The initial attempt was excluded from the controlled result: after the helper requested 144 / Low, the user manually changed selections before starting the game. The helper correctly preserved those manual changes. That launch ran with 90 / High and VDXR logged **2496 × 2688** as the recommended per-eye render resolution, at 1.000 supersampling and 1.000 upscaling. It supplied a comparison point but did not validate the requested preset. The intermediate changes are not evidence of VD spontaneously reverting a preset.

Forefront was then closed. For the controlled retry:

1. The helper applied `VRFramerate=144` and `VRGraphicsQuality=0` (Low) while `HasVR=False` and no Forefront process was running, recorded at approximately 01:38:03 Europe/Kiev on 2026-11-08.
2. The user confirmed **144 FPS / Low** in the headset Streaming panel, without manually changing either selection, before being asked to launch the game.
3. The user launched Forefront from VD's Games tab. `Forefront_Internal.exe` started at approximately 01:38:33. Its VDXR initialization was logged at 01:38:36.
4. At 01:38:41, VDXR logged a recommended per-eye resolution of **1728 × 1824**, still at 1.000 supersampling and upscaling, followed by swapchain creation. This differs from the High-quality comparison at the same reported scale factors.
5. Live helper observations showed `fps=144`, `quality=0`, `effectiveFps=144`, and `HasVR=True`. The user independently reported the in-game VD performance overlay showed **target 144, actual FPS near 144**.

The result supports applying ordinary FPS and quality presets before launching Forefront through VDXR. The resolution log records the runtime's recommendation, not directly measured swapchain dimensions or encoded video dimensions. The overlay FPS is a user observation rather than an automated sustained performance measurement. A preflight ADB read showed an existing Android `debug.oculus.refreshRate=144` override; it was left unchanged. Consequently, this experiment does not independently demonstrate that the helper caused a physical display-mode switch. USB/ADB disconnected during the game test, so no independent Android refresh or VrApi samples were obtained.

This was a manual sequence with an explicit headset check before launch. No launch event was intercepted, and automatic per-game profile selection, acknowledgement gating, other runtimes and other games still require implementation/testing.

After the user closed Forefront, the helper restored the captured **90 FPS / High** selections and cleared its recovery record. The original helper was then restored using the prepared rollback. Evidence is retained in ignored `artifacts/preset-research/forefront-*.xml` and `forefront-*-openxr.txt` files.

### Automatic Forefront launch preset installed and verified

`src/RuntimeOverrides/LaunchPresetRuntime.cs` now intercepts the verified `GameManager` `Task(GameID)` entry (`060001E8`). It emits a matching managed argument adapter rather than assuming a native struct ABI. The original async entry wrapper is reconstructed around VD's untouched launch state machine (`06000235` and verified state/builder/game fields), preserving game discovery, awaits, runtime selection, launching and task failures. The hook is installed once during startup, guarded by executable hash, method/field signatures, x64 architecture and the inspected JIT entry bytes. An absent configuration leaves the hook disabled at startup; removing the file afterward makes Forefront pass through normally.

The installed `%APPDATA%\Virtual Desktop Unlock\LaunchPresets.xml` selects Steam app `2981220`, FPS `144`, quality `0` (Low). Only Forefront's Steam identity is intercepted for preparation; other game identities go directly to VD's original launch continuation. The hook applies settings on the Streamer dispatcher, allows one second for transport/UI synchronization, rechecks the PC selections and initialized connection, then enters the original launch machinery. The one-second interval is not a client acknowledgement. Unavailable settings or a changed selection cancel the configured launch and log a reason. The settings remain selected after exit and are reapplied on the next Forefront launch; no experiment lease or expiry is involved.

Validation passed: the existing companion self-tests and isolated library/bitrate checks; eleven new configuration and preserved async-launch tests; and an isolated real-entry hook test that verifies an unrelated game's value-type identity/continuation and blocks Forefront before its continuation when headset settings are unavailable. The build now writes the app into its unique build directory, avoiding a lock on the already-running companion executable.

For the live automatic test on 2026-11-08 (Europe/Kiev), the user selected 90 FPS / High, reconnected after installation and launched Forefront from VD's Games tab without manually changing either preset. The helper logged preset application at **01:48:35**, release of the original launch at **01:48:36**, and the Forefront process started at **01:48:40**. VDXR identified Forefront at **01:48:43** and logged **1728 × 1824** recommended per-eye resolution at **01:48:48**. The user confirmed that the running game showed **Low quality / target 144 FPS** without manual setting changes. This confirms the intended ordering and preset application for the tested route. The earlier manual test supplied the actual-FPS-near-144 observation; the automatic test confirmed target FPS, not a new sustained throughput measurement.

The automatic helper remains installed as requested. SHA-256: `371E54F51CB96E7BA98A89EE0643E5AEC711BED5C594FFB9363C2DA338D116A1`. The signed Streamer executable remains unchanged. Previous helper/configuration/installation record backups are in `backups/helper-before-forefront-auto-20261108-014816`. Local installation and live evidence are in ignored `artifacts/forefront-presets/`. Deleting or renaming `LaunchPresets.xml` disables automatic preparation without changing the helper binary. This covers VD's Games-tab route, not the companion's Steam URL launch button or launches directly from Steam.

### Launch Presets editor added, 2026-11-08

The companion now has a **Launch Presets** button beside **Launch selected**. It opens a scrollable table containing the actual `SteamProductInfos` entries in VD's cache, independent of the companion's saved additions. Names are resolved from installed/saved games and Steam's local app metadata; cached but uninstalled applications are retained. The local cache contained 40 entries during visual verification.

Each row has an enable checkbox, a quality/resolution slider with VD's nine defined quality tiers, and a discrete FPS slider (72, 80, 90, 96, 100, 120, 144). Existing Forefront Low / 144 loads enabled. Unconfigured rows remain disabled until checked. Save writes the shared XML atomically, keeps a backup, preserves profiles outside the current cache, and rejects a stale window attempting to overwrite another save. Cancel does not change the file.

`Shared/LaunchPresetConfiguration.cs` is used by both UI and runtime to validate the same format. The runtime no longer limits profile matching to Forefront; any configured Steam identity uses the verified launch path. It installs its hook even when no profile file exists, allowing the first UI save to work without another restart. The existing supported binary and Quest 3 restrictions remain. A revised isolated actual-entry test confirmed that a newly saved non-Forefront profile is reloaded and held before the original launcher when headset settings are unavailable.

The complete build, companion tests (including table load/save/cancel and stale-save behavior), localization/layout checks, original launch-state tests and isolated hook tests passed. The window was rendered against the real cache and inspected visually. Both the app and installed helper were updated, retaining the exact existing Forefront configuration. The current helper SHA-256 is `E16F5765E77275EF049E760E7EEB5C1FFAA3A419EC1DBA0188B256DBD7BCBD76`; upgrade receipts and previews are retained in ignored local artifacts. This extends the verified launch mechanism; it is not a new end-to-end performance test of every cached title or quality/FPS combination.

The editor workflow was subsequently changed at the user's request: **Launch Preset** now enables only for exactly one selection in the main window's right-hand VD cache list. It opens a compact editor for that game rather than a cache-wide table. Saving modifies only that game's profile; Cancel and other games' presets are preserved. Selection-gating, single-game save/cancel, localization/layout tests and the build passed, the rendered editor was inspected, and the running app was updated without changing the saved preset XML or restarting the helper.

## Primary references

- [Virtual Desktop FAQ](https://www.vrdesktop.net/): vendor's supported headset Games-tab launch workflow.
- [Microsoft: Win32_ProcessStartTrace](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/krnlprov/win32-processstarttrace): notification occurs after process start.
- [Khronos: XrViewConfigurationView](https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrViewConfigurationView.html): runtime-recommended and maximum image dimensions.
- [Khronos: xrCreateSwapchain](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrCreateSwapchain.html): application creates swapchains using creation parameters.
- [Khronos: xrRequestDisplayRefreshRateFB](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrRequestDisplayRefreshRateFB.html): a refresh-rate request does not guarantee a switch. The PC game and headset client have different runtime sessions; invoking an extension in the PC game is not evidence of controlling the headset client's session.
- [Valve: GetRecommendedRenderTargetSize](https://github.com/ValveSoftware/openvr/wiki/IVRSystem%3A%3AGetRecommendedRenderTargetSize): OpenVR render-target recommendation.
