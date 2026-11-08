# VDXR render resolution and Additional sharpening presets

Investigated 2026-11-08 against the installed x64 Virtual Desktop Streamer **1.34.25.0**, SHA-256 `0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8`.

## Finding

Both settings can be added to the existing per-game launch preset. Their normal PC-side `StreamerSettings` setters and change events forward the settings into the VR configuration or shader parameter buffers. The existing launch hook can call these setters on Streamer's dispatcher before releasing the original game launcher, using the same capture/apply/check/recover sequence as FOV.

This investigation used isolated objects and owned memory. It did not change the installed helper, live settings, game files or headset settings, and did not launch a game. It establishes configuration forwarding, not actual render dimensions, GPU output or visual improvement.

## Settings and verified UI ranges

| Setting | Property | Setter token | UI range | Default metadata |
| --- | --- | --- | --- | --- |
| VDXR Render Resolution | `StreamerSettings.RenderResolutionOverride` | `060003C6` | 50–200% (`0.5f`–`2f`), tick 1%, large step 5% | 100% (`1f`) |
| Additional sharpening enabled | `StreamerSettings.UseSharpening` | `060003CA` | Boolean checkbox | Off |
| Additional sharpening strength | `StreamerSettings.Sharpening` | `060003CC` | 5–100% (`0.05f`–`1f`), tick 1%, large step 5% | 70% (`0.7f`) |

The UI bindings and ranges were read from `mainwindow.baml` using `Baml2006Reader`, without constructing the window. They bind directly to `Default.RenderResolutionOverride`, `Default.UseSharpening` and `Default.Sharpening`. The sharpening strength panel is disabled when `UseSharpening` is false.

Render scale's setter normalizes a value very close to one to exactly one and clamps to 0.5–2.0. Sharpening's strength setter has no equivalent numeric clamp; validate finite values and the UI range in VD Unlock rather than relying on Streamer to reject invalid inputs.

The vendor introduced the VDXR slider in [1.34.8](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.34.8), describes Additional sharpening as Contrast Adaptive Sharpening before video encoding in [1.34.10](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.34.10), and lowered its minimum to 5% in [1.34.12](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.34.12). The installed UI's sharpening tooltip also states that game/SteamVR restart is unnecessary for this control.

## Forwarding and launch timing

### VDXR Render Resolution

`OculusStreamer` subscribes to `RenderResolutionOverrideChanged`. Its original handler (`06000433`) writes the current float into its native VR configuration pointer, at CLR offset **512** in this verified binary. The native configuration initialization path also copies `RenderResolutionOverride`, alongside FOV values. This supports setting it while no game exists, before runtime initialization, as well as updating a configuration block that already exists.

This is a VDXR render-scale control, distinct from the headset's VR Graphics Quality tier and encoded video dimensions. Do not promise equivalent behavior with SteamVR or Oculus/OpenVR routes, a particular pixel resolution, or an area-versus-dimension percentage formula based only on the managed forwarding path. A game can cache its recommended size or create swapchains once; test a fresh process/runtime launch to confirm timing and effective dimensions.

### Additional sharpening

The enable handler (`06000434`) copies `UseSharpening` to two `EffectParameter.SetValue<bool>` targets. The amount handler (`06000435`) copies `Sharpening` to two `EffectParameter.SetValue<float>` targets. These original methods write the CPU-side backing constant buffer and mark it dirty for GPU upload. Shader initialization also reads both settings, so values set before the shaders exist are picked up during initialization.

Additional sharpening is PC-side CAS before encoding. It is distinct from the headset's Streaming-tab sharpening setting. Presetting it should not require altering that headset control.

## Isolated checks

**19 checks passed.** The probe redirected the inspected settings storage directories before singleton initialization, replaced the singleton with a synthetic settings object and installed a no-op save timer. Original Streamer handlers were attached to that object. No running Streamer, encoder or GPU device was used.

- Render-scale updates 0.5, 0.7, 1.0, 1.25 and 2.0 reached the original handler's native float in an owned allocation. Inputs 0.3 and 2.5 clamped to 0.5 and 2.0. A near-one input normalized to one.
- Sharpening on/off/on reached both original effect enable parameters using synthetic `EffectParameter` and `EffectConstantBuffer` objects backed by an owned `SharpDX.DataBuffer`.
- Strengths 0.05, 0.5, 1.0 and 0.7 reached both original effect float parameters. The constant buffer was marked dirty.
- Render scale, sharpening enable and sharpening strength could be restored independently to the synthetic baseline.

The probe, binary, output, extracted implementation details and isolated defaults remain in ignored `artifacts/render-sharpen-research/`. The live `StreamerSettings.json` hash was compared before and after the experiment.

## Proposed preset integration

1. Add an optional Render Resolution override with a 50–200% slider. Disabled means leave the global setting untouched.
2. Add an optional Additional sharpening override with separate On/Off and strength controls. Distinguish “leave unchanged” from “force off”; a strength value alone cannot represent all three intentions.
3. Capture the original render scale, sharpening enable flag and strength durably before applying any setting. Use normal setters on the dispatcher before entering the original launcher, then verify readback.
4. For an enabled sharpening preset, set the strength before enabling it. Restore the captured strength and enabled state with the existing manual-change and overlapping-session protections. Treat the two sharpening values as independently recoverable settings.
5. Extend model validation and recovery serialization. Read existing formats; emit a new format version when new fields are present so a 0.2.1 helper rejects unsupported configuration rather than silently ignoring it. Update the companion app and helper together.

## Remaining live validation

Use Forefront with fixed quality, FPS and FOV. Compare fresh VDXR launches at 100% and a distinct render scale, recording the VDXR recommendation and actual swapchain dimensions where available. Check that settings were applied before runtime initialization. Separately compare Additional sharpening Off and On at a fixed strength to confirm its visible effect, then verify exit recovery and manual changes. Existing runtime sessions and non-VDXR game routes need separate checks.

No feature implementation, live installation or version change was made as part of this investigation.

## 0.2.2 implementation follow-up

The 0.2.2 build implements the proposed optional sliders, explicit sharpening enable state, before-launch application and durable per-setting recovery. It emits format 3 only when new fields are present and reads formats 1–3. Automated editor and recovery checks and the helper float/Boolean bridge through original native/shader forwarding passed. Live game and GPU output verification remains pending. This follow-up does not install or enable the new helper on the test PC.

## Live Forefront validation

On 2026-11-08, the matching 0.2.2 helper was installed and the companion app was opened on the normal Windows desktop for the user's test. Two Forefront launch-and-exit cycles were monitored:

- At 15:52:41 (Europe/Kiev), Additional sharpening On / 100% was applied before the original launcher was released at 15:52:42. Forefront was observed running at 15:52:46. After exit, recovery completed at 15:55:39, restoring sharpening Off and its captured strength of approximately 50%, as well as the original FPS, quality and FOV.
- At 15:56:12, VDXR render scale 50% and Additional sharpening On / 100% were applied. The original launcher was released at 15:56:13, and Forefront was observed running at 15:56:17. VDXR initialized at 15:56:20 and reported **1004 × 1096 recommended per-eye resolution, 0.500 supersampling and 1.000 upscaling** at 15:56:24. This is the runtime recommendation, not an independently measured game render target or encoded stream size.
- After the second exit, recovery completed at 15:59:57. Persisted settings returned to render scale 100%, sharpening Off with approximately 50% strength, and both FOV axes 100%. The helper reported original 90 FPS / High quality, and the pending recovery file cleared.

The user reported “Finished; everything works.” No helper errors were observed in these two cycles. This supports the tested Forefront/VDXR route, including restoration; it does not quantify CAS image quality or prove behavior in other games or runtimes. Evidence remains in ignored `artifacts/live-022/`.

The final editor uses a single Additional sharpening override checkbox: checking it enables sharpening at the selected strength; clearing it leaves global sharpening unchanged. The separate On/Off checkbox was removed at the user's request. The runtime retains support for older explicit-Off records and for restoring an original disabled state.
