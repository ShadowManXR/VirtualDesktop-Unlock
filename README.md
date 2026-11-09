<p align="center"><img src="assets/icon.png" width="112" height="112" alt="Virtual Desktop Unlock icon" /></p>

# Virtual Desktop Unlock

**v0.2.2** · Windows x64 · Unofficial · MIT

An unofficial program for extending Virtual Desktop Streaming. Add custom Steam games, including games that use third-party VR mods, to Virtual Desktop's actual headset Games tab using the optional runtime helper. The desktop companion app organizes your installed Steam games, saves selections for the helper, and provides optional headset-specific bitrate ceilings. There is also an option to set application launch presets, such as for FPS, quality, and FOV tangent.

![Virtual Desktop headset Games panel showing custom Steam games, including Gothic and Resident Evil](screenshots/VirtualDesktop.Android-20261022-125310.jpg)

## Unlock features

| Feature | Description |
|---|---|
| Third-party games | Adds custom Steam IDs to Streamer's game discovery. There is also an option to add games from Venture to the Oculus Games category (this feature has not yet been tested on the Pico 4). |
| Game launch presets | Allows you to set custom launch parameters, such as FPS, quality, FOV tangent, VDXR render resolution, and sharpening. These settings are applied before the game launches. Once the game is closed, the settings revert to their original values ​​as configured in VD. |
| Unlimited bitrate | Allows you to set your own bitrate limit for Wi-Fi or USB connections, though there is no guarantee that the stream will work correctly. This is an experimental feature. |

## Download and run

Download the archive from [Release](https://github.com/ShadowManXR/VirtualDesktop-Unlock/releases)

## Future plans

| Feature | Description |
|---|---|
| FOV Tangent Fill-Reconstruct | The FOV tangent sets reduced rendering dimensions, which results in the appearance of black areas; these can be filled using various methods, such as gradients, blurring, or mirroring. |
| Anti-drift stick | Handle stick presses when a finger is resting on the stick, by leveraging the ability to detect finger contact with the stick. A partial solution. |
| Third-party games not on Steam | Add any VR .exe application, complete with an image, so that it appears in the VD game panel. |

## Build

Install the .NET 10 SDK on Windows x64. The build also uses the .NET Framework C# compiler and WPF reference assemblies supplied with Windows. The app has no third-party NuGet dependencies.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The build runs the companion UI/storage tests and an AppDomain-manager loading test, then creates the release ZIP and SHA-256 checksum in `artifacts/dist/`. For the additional delayed-initialization test against your own supported Streamer installation:

```powershell
.\build.ps1 -StreamerPath 'C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe'
```

That test uses an isolated configuration directory and does not launch a VR game or install the integration. A build never changes your current Streamer installation.

## Source map

- `SteamVRLibrary/`: WinForms companion app and tests.
- `src/RuntimeOverrides/`: .NET Framework startup helper, configuration, and test harness.
- `scripts/`: optional integration installer, uninstaller, and launchers.
- `assets/`: edited Streamer-derived icon master, PNG, Windows ICO, and artwork attribution.
- `tools/generate-icon.py`: icon packager (Python + Pillow), needed only when regenerating icon sizes.
- `docs/verification.md`: compatibility and verification scope.

The MIT license applies to this project's original code. The icon incorporates Virtual Desktop branding; see [artwork attribution](assets/README.md). Install Virtual Desktop and Steam separately from their official sources.
