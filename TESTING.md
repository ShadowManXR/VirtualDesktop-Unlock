# Virtual Desktop Unlock 0.3.0-alpha validation guide

This build adds experimental VDXR render resolution and Additional sharpening launch presets for the verified Virtual Desktop Streamer 1.34.25.0 x64 / Quest 3 setup. The user confirmed that the Forefront preset works at 70% horizontal / 70% vertical with the 0.2.1 helper. In 0.2.2, monitored Forefront tests also confirmed Additional sharpening and 50% VDXR render-scale application and exit recovery; the user reported everything working. Other games and runtimes still need individual verification.

## Install or upgrade

1. Close games and exit the previous Virtual Desktop Unlock app from its tray menu.
2. Extract the ZIP into a new writable folder. Copy your previous app's `Data` folder into it to retain library selections. Shared launch presets are already stored in `%APPDATA%\Virtual Desktop Unlock`.
3. Run **Install Streamer Helper.exe**, approve the Windows prompt, and confirm the detected Streamer installation. Choose **No** to browse to a different installation. Setup restarts Streamer and can upgrade a helper whose files match its existing installation record. To remove the helper from a custom location, run the setup executable with `--uninstall`.
4. Open **Virtual Desktop Unlock.exe** from this build and reconnect VD to the PC. The app requires .NET Desktop Runtime 10. The advanced launch presets require helper 0.2.2 or later.

## Venture Oculus Games

Use the **0.3.0-alpha** app and helper with verified Streamer **1.34.25.0** and the Venture launcher installed in its default Windows location. VD Unlock scans Venture's default game folder, its configured primary folder, and any additional library folders. Confirm **Show Venture games in Oculus Games** starts unchecked. Enable it and restart Streamer after Venture shows the game as playable. To verify disabling, clear the checkbox, restart Streamer, and confirm the Venture tiles are gone and regular Oculus tiles remain.

Select **VDXR** as the OpenXR runtime in Streamer and reconnect the headset. Open **Games → Oculus Games**, check the original artwork, and select Play. Once the game is running, reopen Games and use **X** on its tile. Check that the game closes and the tile returns to Play. If it fails, check Venture's logs and `%ProgramData%\Virtual Desktop\LibraryOverrides.log` for the game list and supervisor messages.

## Per-game button remaps

Install the helper included with the remap test build and select **VDXR** explicitly in Streamer. On Quest 3, select a Steam title in **VD cache games**, open **Remap Buttons**, and use **+** to add a pair such as **A ↔ B**. Save and launch it from Virtual Desktop's **Games** tab. Verify both directions of the swap, then quit and launch another title to verify that it uses its own mapping. Use **−** and save to remove a pair.

Swap the left and right triggers and verify partial pressure on each side. Repeat for the grips. Swap the stick clicks and verify that stick movement stays on its original side.

## Test a launch and exit

1. Keep Forefront closed. In VD's Advanced settings, note your original horizontal and vertical FOV tangent values; also note the current FPS and quality.
2. Select **Forefront** in **VD cache games**, then open **Launch Preset**.
3. Enable **Use preset**, select your FPS and quality (for example, 144 FPS / Low), and enable **Override FOV tangent (experimental)**. Start with **90% horizontal / 90% vertical** and save.
4. Launch Forefront from VD's **Games** tab. Check FPS, quality and FOV selections, and whether the visible rendering changes. FOV values are tangent multipliers: 90% means 0.90, not 90 degrees.
5. Quit the game and wait a few seconds. Verify that FPS, quality and both FOV axes return to their original selections. Values changed manually during play should be preserved instead of restored.
6. Disable **Override FOV tangent** and save to return to an FPS/quality-only preset. Opening or saving the editor does not immediately change VD settings; presets apply at the next launch through VD.

The helper uses the original Streamer FOV setters before entering its launcher. Automated tests verify values reaching an isolated native configuration block, rather than a running headset. Actual rendering changes and compatibility with each game's VR runtime need this manual test.

## Test VDXR render resolution and Additional sharpening

1. Keep Forefront closed. Note the original **VDXR Render Resolution**, **Additional sharpening** On/Off state and sharpening strength in the PC Streamer Advanced tab. Keep FPS, quality and FOV fixed while comparing.
2. In Forefront's Launch Preset, enable **Override VDXR render resolution (experimental)** and select a distinct value, for example **125%**. Scroll down to enable **Override additional sharpening (experimental)**, select **50%**, and save.
3. Launch Forefront through VD's Games tab using VDXR. Verify the PC Advanced-tab selections. Compare runtime render recommendations or swapchain dimensions if available, and compare sharpening visually with a separate Off launch. The encoded stream dimensions need not change with VDXR render scale.
4. Quit the game and wait a few seconds. Verify the original render scale, sharpening On/Off state and strength are restored. On another run, manually change one setting and confirm exit preserves it while restoring the others.
5. **Override additional sharpening** enables sharpening at launch using the selected strength. To leave global sharpening unchanged, clear this override. Clear the render override to leave the global render scale unchanged.

Opening or saving the editor does not apply settings immediately. Tests cover native configuration and CPU-side shader buffer forwarding, not actual GPU image output. Use a fresh game/runtime session when comparing VDXR render dimensions.

## Logs and rollback

Launch preparation and recovery are logged in `%PROGRAMDATA%\Virtual Desktop\LibraryOverrides.log`. Installer output is in `%PROGRAMDATA%\Virtual Desktop Unlock\installer.log`.

Render resolution and sharpening presets and pending recovery use XML format version 3. Before downgrading, close the game, keep VD connected until restoration completes, then disable both new overrides in every configured game and save. FOV-only records use format 2 and require helper 0.2.1 or later; disable FOV too when reverting to a pre-0.2.1 build. Confirm `%APPDATA%\Virtual Desktop Unlock\LaunchPresetRecovery.xml` has cleared. Uninstall this helper, then reinstall the previous build's helper and run its app. Older apps and helpers do not support the new format 3 records.
