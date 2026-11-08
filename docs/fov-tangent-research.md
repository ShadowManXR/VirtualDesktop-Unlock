# FOV tangent launch-preset feasibility

Investigated 2026-11-08 against the installed, verified Virtual Desktop Streamer 1.34.25.0 executable, SHA-256 `0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8`. This is analysis and an isolated PC-side experiment; no FOV preset feature was installed or enabled.

## Finding

Adding per-game horizontal and vertical FOV tangent overrides to the existing launch preset is technically feasible. The original settings setters and their original Streamer event handlers were tested together: changes reached the native VR configuration block. This is stronger evidence than setting a managed property alone, but does not demonstrate a real game's resulting projection, render dimensions or performance.

VD already exposes these controls in its Advanced tab. The vendor's [1.34.2 release notes](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.34.2) document the addition of FOV Tangent sliders and separately describe the FOV Stencil option.

## Local call path

| Property | Setter token | Default metadata | Setter clamp |
| --- | --- | --- | --- |
| `StreamerSettings.HorizontalFovTangent` | `060003C2` | `1.0f` | `0.4f` to `1.0f` |
| `StreamerSettings.VerticalFovTangent` | `060003C4` | `1.0f` | `0.4f` to `1.0f` |

Both setters update their backing field, raise the normal property-change notification and raise an axis-specific change event. `OculusStreamer` subscribes to both events. Its existing handlers (`06000431`, `06000432`) read the current settings and write the corresponding float into the native VR configuration block. Its configuration initialization routine also copies both values before native runtime use. The verified native fields are distinct, at CLR offsets 504 and 508 in this binary.

These settings belong to PC-side `StreamerSettings`, unlike FPS and quality in `SharedUserSettings`. Their demonstrated forwarding path does not depend on receiving a headset settings message. This distinction does not prove the native backend's exact consumption timing.

The values are normalized tangent controls, suitable for a UI range of **40–100%** with **100% / 100%** as stock. They are not FOV angles in degrees; an 80% tangent factor should not be described as 80 degrees or necessarily 80% of the angular field of view. OpenXR itself expresses per-eye FOV using four angles in radians: [Khronos XrFovf](https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrFovf.html).

## Isolated experiment

Fifteen checks passed using the original installed binary. An independent .NET Framework process redirected the two inspected storage-directory fields into an ignored research directory before accessing the settings singleton. The generic singleton's type initializer created an isolated default instance and defaults file; no live preferences were loaded. It was then replaced with a synthetic settings object. A no-op save timer on that synthetic object allowed the tested setters to run without persisting their changes. The original axis event handlers were attached to the synthetic settings object and pointed at an owned, zero-initialized unmanaged allocation in a synthetic Streamer owner.

The checks established the shared native configuration pointer and distinct float fields, then exercised each original setter with 0.9, 0.8, 0.4, 0.2, 1.2 and 1.0. Inputs 0.2 and 1.2 clamped to 0.4 and 1.0 respectively. Property readback and the native float matched on each operation, while the other axis remained unchanged. FOV Stencil remained unchanged. Both axes finished at 1.0 in the synthetic object, and the owned allocation/timer were released.

The probe, output, isolated defaults and proprietary IL remain in ignored `artifacts/fov-tangent-research/`. No network peer, encoder, VR session or full Streamer application was started. The live `StreamerSettings.json` hash was unchanged afterward. The running Streamer, installed helper, game files, headset settings and live FOV selections were not modified.

## Proposed integration

- Add optional horizontal/vertical FOV values to the shared preset model and XML, preserving existing profiles that omit them. Leave global FOV unchanged when the override is disabled. Update the UI and helper together so older readers/writers cannot silently ignore or discard the new fields.
- Add an opt-in FOV override and two 40–100% controls in the selected-game editor. Validate finite numeric values explicitly and serialize with invariant culture. Use the normal settings setters on Streamer's dispatcher before releasing the existing original game-launch continuation.
- Extend the durable recovery record with each overridden axis's original and applied float values. Restore axes independently, with a small floating-point tolerance, while preserving subsequent manual changes. Retain support for existing recovery records without FOV fields and for overlapping configured sessions.
- Leave `UseFovStencil` unchanged: it is a separate setting, not necessary merely to set the tangent properties.

## Remaining validation

A controlled live comparison is still needed, with no existing game/session using stale configuration: fix FPS/quality and compare 100%/100% with a modest reduction, then restore. Verify the Advanced-tab selections, game-visible FOV/projection, runtime-recommended render size where observable, and exit recovery. Changing the global settings before process creation should place them before the game's runtime initialization, but an already running SteamVR/VDXR session may retain configuration and must be tested separately.

Do not infer a particular pixel resolution, angular reduction or guaranteed performance gain solely from the tangent percentage. Reducing FOV can make the rendered edge/crop visible and expose game-specific culling behavior. Those effects are outside the isolated forwarding checks.

## Implementation follow-up

Version 0.2.1 implements the optional FOV sliders, native settings forwarding and per-axis recovery described above. The user confirmed the Forefront preset works at 70% horizontal / 70% vertical after the installed helper was updated from 0.1.8 to 0.2.1. The old helper rejected format version 2 before starting the game; the UI and helper must be updated together. This confirmation does not establish behavior for every game or runtime.
