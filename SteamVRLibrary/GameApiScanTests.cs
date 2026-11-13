namespace SteamVRLibrary;

internal static class GameApiScanTests
{
    internal static void Run(Action<bool, string> check)
    {
        string[] vdxr =
        [
            "2026-11-13 12:34:22 +0200: VirtualDesktopOpenXR - v1.1.0 (build)",
            "2026-11-13 12:34:22 +0200: Application: Example VR (example.exe); Engine: Example",
            "2026-11-13 12:34:22 +0200: Application requests API version 1.0.63",
            "2026-11-13 12:34:22 +0200: Using Virtual Desktop runtime",
            "2026-11-13 12:35:22 +0200: Application: Another (other.exe); Engine: Example",
            "2026-11-13 12:35:22 +0200: Application requests API version 1.1.50"
        ];
        var xr = GameApiScan.ReadVdxr(vdxr, name => name == "example.exe").Single();
        check(xr is { Api: "OpenXR", Version: "1.0.63", RuntimeVersion: "1.1.0" } && xr.Started.Offset.Hours == 2,
            "VDXR scan keeps the requested API version separate from the runtime and other applications");
        check(!GameApiScan.ReadVdxr(vdxr, name => name == "other.exe").Any(), "Unconfirmed VDXR launch is skipped");

        const string prefix = "Fri Nov 13 2026 12:15:18.653 [Info] - ";
        string[] steam =
        [
            prefix + "Example 2.18.4 startup with PID=123, config=path, runtime=path",
            prefix + "vrclient type=VRApplication_Scene",
            prefix + "Received success response from vrserver connect",
            prefix + "App key after connect message:steam.app.123",
            prefix + "OpenVR SDK version 1.16.8",
            prefix + "RendermodelManager: name=openxr_aim",
            prefix + "Example 2.18.4 startup with PID=124, config=path, runtime=path",
            prefix + "vrclient type=VRApplication_OpenXRInstance",
            prefix + "Received success response from vrserver connect",
            prefix + "App key after connect message:steam.app.123"
        ];
        var sessions = GameApiScan.ReadSteam(steam, "123", false).ToArray();
        check(sessions.Length == 2 &&
            sessions[0] is { Api: "OpenVR", Version: "1.16.8", RuntimeVersion: "2.18.4" } &&
            sessions[1] is { Api: "OpenXR", Version: null, RuntimeVersion: "2.18.4" },
            "SteamVR scan distinguishes OpenVR and OpenXR sessions and keeps versions separate");
        check(!GameApiScan.ReadSteam(steam, "999", true).Any(), "A conflicting Steam app ID rejects the session");
    }
}
