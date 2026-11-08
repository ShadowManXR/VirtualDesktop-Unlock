# Quest 3 refresh rates above 144 Hz

Research date: 2026-10-30. This is a feasibility investigation, not a working FPS-unlock feature.

## Finding

A PC-side override of the synchronized `SharedUserSettings.VRFramerate` is a promising route to request 180, 200 or 207 Hz in the existing headset client. Changing only a PC encoder frame-rate getter would be insufficient: the headset must receive the setting and request the matching display mode. Actual streamed FPS above 144 has not been tested in Virtual Desktop.

## Platform support

[Meta's native display refresh-rate documentation](https://developers.meta.com/vr/documentation/native/android/mobile-display-refresh-rate/) currently documents 144, 180, 200 and 207 Hz requests on Quest 3 with HorizonOS v2.7 or later. Quest 3S is excluded. Meta identifies 207 Hz as the maximum for full-resolution display delivery. Rates above that require developer-mode display scaling, which reduces image quality. Scaling is unnecessary for the proposed 200 Hz experiment.

Meta documents `xrRequestDisplayRefreshRateFB` for requesting a mode and `xrGetDisplayRefreshRateFB` for checking the actual mode. Older OpenXR applications can request the specifically documented extended rates even when their enumeration omits them.

## Local evidence

- Installed Streamer and headset client: 1.34.25.0.
- Streamer SHA-256: `0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8`.
- Read-only ADB display diagnostics on the connected Quest 3 listed 4128 × 2208 modes at 200 and approximately 207 Hz. The display was off when inspected; its selected mode reported approximately 144 Hz. These are display capabilities and selected state, not measured streaming throughput.
- The PC `VirtualDesktop.Mobile.Framerate` enum has named options through `Framerate144`.
- In a separate .NET Framework process with isolated settings and synthetic Quest 3 settings objects, the original `SharedUserSettings.VRFramerate` setter and `SharedMobileSettings.VRFramerate` getter preserved 120, 144, 180, 200 and 207 without clamping. The Streamer's JSON library also round-tripped all five numeric enum values, including those without enum names: ten checks passed.
- These checks did not initialize a network connection, run an encoder, or change the running Streamer or its settings. Successful JSON round-tripping alone does not prove successful network negotiation.

## Headset client inspection

A local copy of the installed APK was inspected without changing or reinstalling it. Its .NET Android assembly store was read according to the [dotnet/android assembly-store format](https://github.com/dotnet/android/blob/main/Documentation/project-docs/AssemblyStores.md). Proprietary binaries and extracted code remain in ignored local research artifacts.

The inspected managed call path is:

1. `VirtualDesktop.Mobile.SharedUserSettings.set_VRFramerate` stores the numeric enum and raises change notifications; it contains no enum-membership validation.
2. `VirtualDesktop.Net.NetworkSettingsBase<T>` sends settings/property changes through JSON and populates received properties. Live synchronization of an extended value still requires testing.
3. `VirtualDesktop.Mobile.SharedMobileSettings.get_VRFramerate` supplies the selected value for Quest 3.
4. In VR streaming mode, `VirtualDesktop.Mobile.Scene.AdjustPerformanceSettings` reads that value and passes it to `HMD.SetPerformanceOptions`.
5. `Xenko.VR.OpenXRHMD.SetPerformanceOptions` passes the requested rate to `Xenko.OpenXR.Session.RequestDisplayRefreshRate`.
6. That wrapper invokes `xrRequestDisplayRefreshRateFB`. It discards the returned result. The HMD code then stores the requested rate and calculates its frame time from it, regardless of whether the runtime accepted the request.

The headset's streaming UI exposes buttons only through 144 FPS. The inspected UI change handler updates those buttons and dependent feature availability; it does not clamp an unknown positive setting to 144. This supports trying a synchronized numeric override before considering any client modification, but does not establish compatibility of the entire video pipeline.

## Proposed implementation and validation

An experimental helper could wait for an initialized Quest 3 connection, capture the current selection, set `SharedUserSettings.VRFramerate` to 180 or 200 through its normal setter, and allow the existing synchronization/events to propagate it. It should be guarded by the inspected Streamer version/hash, restricted to Quest 3, and restore the captured selection when disabled. Reconnection and user changes need explicit handling. No helper or UI change has been installed as part of this investigation.

Start with 180 Hz and then 200 Hz at a modest resolution and bitrate, without spacewarp, to distinguish actual source frames from synthesized frames. Validate all of the following independently:

- The shared setting arrives at the headset and is not reset on reconnect or when its settings panel opens.
- The Meta runtime's actual refresh rate changes, checked with runtime metrics rather than only VD's requested-value display.
- SteamVR or VDXR uses the matching timing, and encoder/video delivery sustains the requested rate.
- Decoder throughput, dropped/repeated frames, buffering, latency and thermal behavior remain acceptable over a sustained session.
- Disabling restores the prior setting and display timing.

At 200 FPS the game has a 5 ms frame budget. Encoder and decoder throughput must also sustain 200 frames per second; pipelined decoder latency can exceed 5 ms, so latency alone is not a throughput measurement.

An [ALVR discussion](https://github.com/alvr-org/ALVR/discussions/3410) contains a user's reported 200 FPS experiment using different decoder buffering and low-latency settings. It is useful as an experimental lead, not independently verified evidence that Virtual Desktop can sustain that rate. Its settings should not be applied blindly to VD.

## Remaining uncertainty

The PC settings path and inspected client call path support an experiment. Actual OpenXR acceptance, live settings propagation, runtime timing, native encoder behavior, decoder throughput and stable 200 FPS streaming remain unverified. No claim of a working unlock is warranted yet.

## Initial live experiment, 2026-10-30

With the user's explicit approval, a temporary helper was installed with backups of the original helper and installation record. It keeps the existing library behavior, accepts only short-lived Quest 3 FPS commands, records the previous selection, and restores it when the command expires or is removed. Fifteen isolated experiment-control checks and the library-loading check passed.

The initial monitor incorrectly looked up the network connection property by its unobfuscated name. The corrected monitor uses the exact method used by the installed Streamer's settings send path (`06021427`), guarded by the verified executable hash. The running helper then confirmed initialized, connected Quest 3 settings.

The baseline VD selection was 144 FPS. Independently, the headset already had `debug.oculus.refreshRate=200` set before any experiment command. Its initial 200 Hz display mode therefore does not demonstrate synchronization by this helper.

The original settings setter changed the running PC selection and effective VR setting to 200, and subsequently to 180 and 200 for a short comparison. For that comparison the pre-existing Android override was temporarily cleared; it was restored to 200 afterward. VD's selected rate was restored to 144, with no pending recovery record. During the comparison the headset display remained at 72 Hz and logged approximately 72 FPS. The Streamer restart had interrupted the prior VR session, so this does not settle behavior in an active VR game. A resumed VR session is required for the next measurement. Client synchronization and 200 FPS video delivery are not yet established.

### Repeated live test after resuming VR

The user confirmed returning to the VR game. A repeated 180/200 comparison still showed only the PC setting changing: Android's actual display mode remained approximately 72 Hz and the VD process's VrApi logs reported roughly 72–73/72 FPS. Both settings were restored afterward.

A subsequent three-minute 200 FPS test included the user opening and closing VD's own control panel, to trigger `Scene.AdjustPerformanceSettings`. Display samples continued to report 72 Hz despite the PC monitor reporting selected/effective 200. The real Streamer property-message serializer was also tested separately: it emitted valid `{"VRFramerate":180}` and `{"VRFramerate":200}` payloads. Serialization of unnamed enum values is therefore supported, but it does not prove that a live peer received or applied them.

The tested PC setter approach has not produced a working 200 Hz client mode. The remaining failure could be live message propagation, session/application state, or runtime rejection; the client wrapper ignores the OpenXR return code, and there is no direct client-setting acknowledgement in the diagnostic helper. Neither a 200 FPS label on the PC nor the pre-existing Android force-rate property establishes 200 FPS streamed video. Further work needs direct client-side receipt/request diagnostics before adding an FPS-unlock control to the application.

After testing, VD's original 144 FPS selection and the pre-existing Android `debug.oculus.refreshRate=200` property were restored, with no pending recovery record. The original helper and installation record were restored from their verified backup. The restored helper SHA-256 is `179FC5B9C756C669ADCEBC791A46300B64F7448A5C81FC819252C0001FE64D5B`. Streamer restarted with its developer signature still valid and all 18 custom game overrides logged as applied. No experimental FPS helper remains installed.
