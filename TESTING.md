# Virtual Desktop Unlock 0.2.1 validation guide

This build adds experimental FOV tangent launch presets for the verified Virtual Desktop Streamer 1.34.25.0 x64 / Quest 3 setup. The user confirmed that the Forefront preset works at 70% horizontal / 70% vertical with the 0.2.1 helper. Other games and runtimes still need individual verification.

## Install or upgrade

1. Close games and exit the previous Virtual Desktop Unlock app from its tray menu.
2. Extract the ZIP into a new writable folder. Copy your previous app's `Data` folder into it to retain library selections. Shared launch presets are already stored in `%APPDATA%\Virtual Desktop Unlock`.
3. If a helper is installed, run **Uninstall RE3 Integration.cmd** from the previous build. Then run **Install RE3 Integration.cmd** from this build. Both operations request administrator permission and restart Streamer. The installer refuses to overwrite another version directly.
4. Open **Virtual Desktop Unlock.exe** from this build and reconnect VD to the PC. The app requires .NET Desktop Runtime 10. The app and helper must both be 0.2.1 for FOV presets.

## Test a launch and exit

1. Keep Forefront closed. In VD's Advanced settings, note your original horizontal and vertical FOV tangent values; also note the current FPS and quality.
2. Select **Forefront** in **VD cache games**, then open **Launch Preset**.
3. Enable **Use preset**, select your FPS and quality (for example, 144 FPS / Low), and enable **Override FOV tangent (experimental)**. Start with **90% horizontal / 90% vertical** and save.
4. Launch Forefront from VD's **Games** tab. Check FPS, quality and FOV selections, and whether the visible rendering changes. FOV values are tangent multipliers: 90% means 0.90, not 90 degrees.
5. Quit the game and wait a few seconds. Verify that FPS, quality and both FOV axes return to their original selections. Values changed manually during play should be preserved instead of restored.
6. Disable **Override FOV tangent** and save to return to an FPS/quality-only preset. Opening or saving the editor does not immediately change VD settings; presets apply at the next launch through VD.

The helper uses the original Streamer FOV setters before entering its launcher. Automated tests verify values reaching an isolated native configuration block, rather than a running headset. Actual rendering changes and compatibility with each game's VR runtime need this manual test.

## Logs and rollback

Launch preparation and recovery are logged in `%PROGRAMDATA%\Virtual Desktop\LibraryOverrides.log`. Installer output is in `%PROGRAMDATA%\Virtual Desktop Unlock\installer.log`.

FOV presets and pending recovery use XML format version 2. Before downgrading, close the game, keep VD connected until restoration completes, then disable FOV overrides in every configured game and save. Confirm `%APPDATA%\Virtual Desktop Unlock\LaunchPresetRecovery.xml` has cleared. Uninstall this helper, then reinstall the previous build's helper and run its app. Older apps and helpers do not support FOV records.
