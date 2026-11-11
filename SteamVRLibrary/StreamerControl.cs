using System.Diagnostics;
using System.Globalization;

namespace SteamVRLibrary;

public interface IStreamerControl
{
    bool IsRunning { get; }
    bool IsApplied(IReadOnlyCollection<uint> ids);
    Task RestartAsync();
}

public sealed class StreamerControl : IStreamerControl
{
    private const string ProcessName = "VirtualDesktop.Streamer";
    private const string AppliedMarker = "Installed OpenXR overrides: ";

    public bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName(ProcessName);
            // Enumeration already identifies running processes. HasExited opens a
            // synchronization handle that Windows may deny for an elevated Streamer.
            // A process exiting after this snapshot is handled by the next refresh.
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer");
    private static string Executable => Path.Combine(Folder, "VirtualDesktop.Streamer.exe");
    private static string Log => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Virtual Desktop", "LibraryOverrides.log");

    public bool IsApplied(IReadOnlyCollection<uint> ids)
    {
        try
        {
            var lines = File.ReadAllLines(Log);
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    string prefix = $"[{process.Id}] {AppliedMarker}";
                    var line = lines.LastOrDefault(l => l.Contains(prefix, StringComparison.Ordinal)
                        && MatchesProcessStart(l,process.StartTime.ToUniversalTime()));
                    if (line == null) continue;
                    var value = line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Split(';')[0];
                    var applied = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToHashSet();
                    if (applied.SetEquals(ids)) return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or FormatException) { }
        return false;
    }

    internal static bool MatchesProcessStart(string line,DateTime startedUtc)
        => DateTimeOffset.TryParse(line.Split(' ')[0],CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)
            && time.UtcDateTime >= startedUtc.AddSeconds(-1);

    public async Task RestartAsync()
    {
        // Use a separate elevated instance only for restarting the installed Streamer.
        // The library window continues running without administrator privileges.
        using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart-streamer")
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })
            ?? throw new IOException(Localizer.T("Could not start the Streamer restart helper."));
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0)
        {
            AppLog.Current.Warning($"Streamer restart helper returned exit code {helper.ExitCode}.");
            throw new IOException(Localizer.T("Could not restart Streamer. Check that Streamer and the library integration are installed, then try again."));
        }
        AppLog.Current.Info("Streamer restarted.");
    }

    internal static int RestartInstalledStreamer()
    {
        try
        {
            if (!File.Exists(Executable) || !File.Exists(Path.Combine(Folder, "VirtualDesktop.LibraryOverrides.dll"))
                || !File.Exists(Executable + ".config"))
            {
                AppLog.Current.Warning("Cannot restart Streamer because the executable or integration files are missing.");
                return 2;
            }
            using var current = Process.GetCurrentProcess();
            var sessionId = current.SessionId;
            var previous = new HashSet<int>();
            var processes = Process.GetProcessesByName(ProcessName);
            try
            {
                foreach (var process in processes)
                {
                    if (!IsInstalledStreamer(process, sessionId)) continue;
                    previous.Add(process.Id);
                    process.CloseMainWindow();
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000))
                        {
                            AppLog.Current.Warning($"Streamer process {process.Id} did not exit during restart.");
                            return 3;
                        }
                    }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }

            // Streamer's service can relaunch it as the previous process exits.
            // Give that replacement time to load the integration before starting another.
            var elapsed = Stopwatch.StartNew();
            var nextLaunch = TimeSpan.FromMilliseconds(750);
            var launches = 0;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (TryFindReplacement(sessionId, previous, out var readyPid))
                {
                    if (readyPid.HasValue)
                    {
                        AppLog.Current.Info($"Streamer process {readyPid} loaded the library integration after restart.");
                        return 0;
                    }
                }
                else if (launches < 3 && elapsed.Elapsed >= nextLaunch)
                {
                    launches++;
                    using var started = Process.Start(new ProcessStartInfo(Executable)
                    { WorkingDirectory = Folder, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                    if (started == null) AppLog.Current.Warning($"Streamer launch attempt {launches} did not return a process.");
                    else if (started.WaitForExit(750))
                        AppLog.Current.Warning($"Streamer launch attempt {launches} exited with code {started.ExitCode}.");
                    nextLaunch = elapsed.Elapsed + TimeSpan.FromMilliseconds(750);
                }
                Thread.Sleep(250);
            }
            AppLog.Current.Warning($"No replacement Streamer process loaded the library integration after {launches} launch attempt(s).");
            return 4;
        }
        catch (Exception error)
        {
            AppLog.Current.Error("Could not restart the installed Streamer", error);
            return 1;
        }
    }

    private static bool IsInstalledStreamer(Process process, int sessionId) =>
        !process.HasExited && process.SessionId == sessionId &&
        string.Equals(process.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase);

    private static bool TryFindReplacement(int sessionId, HashSet<int> previous, out int? readyPid)
    {
        readyPid = null;
        var found = false;
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (previous.Contains(process.Id) || !IsInstalledStreamer(process, sessionId)) continue;
                    found = true;
                    var startedUtc = process.StartTime.ToUniversalTime();
                    if (DateTime.UtcNow - startedUtc < TimeSpan.FromSeconds(2) || !File.Exists(Log)) continue;
                    var marker = $"[{process.Id}] {AppliedMarker}";
                    if (File.ReadLines(Log).Any(line => line.Contains(marker, StringComparison.Ordinal)
                        && MatchesProcessStart(line, startedUtc)) && !process.HasExited)
                    {
                        readyPid = process.Id;
                        break;
                    }
                }
                catch (InvalidOperationException) { /* The process exited while being inspected. */ }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
                catch (IOException) { /* Streamer may be writing the log. Check it again on the next poll. */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return found;
    }
}
