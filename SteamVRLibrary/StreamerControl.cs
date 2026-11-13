using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

public interface IStreamerControl
{
    bool IsRunning { get; }
    bool IntegrationInstalled { get; }
    bool IsApplied(IReadOnlyCollection<uint> ids);
    bool IsFoveaSizeApplied(int size) => false;
    Task RestartAsync();
    Task InstallIntegrationAsync();
}

public sealed class StreamerControl : IStreamerControl
{
    private const string ProcessName = StreamerLocation.ProcessName;
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
    private static string Log => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Virtual Desktop", "LibraryOverrides.log");

    public bool IntegrationInstalled => HasIntegration(StreamerLocation.FindInstalledHelperTarget());

    internal static bool HasIntegration(string executable)
    {
        if (!File.Exists(executable)) return false;
        var helper = Path.Combine(Path.GetDirectoryName(executable)!, "VirtualDesktop.LibraryOverrides.dll");
        try
        {
            var assembly = AssemblyName.GetAssemblyName(helper);
            using var reader = XmlReader.Create(executable + ".config", new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 });
            var runtime = XDocument.Load(reader).Root?.Element("runtime");
            var configured = (string?)runtime?.Element("appDomainManagerAssembly")?.Attribute("value");
            return assembly.Name == "VirtualDesktop.LibraryOverrides" && configured != null &&
                new AssemblyName(configured).FullName == assembly.FullName &&
                (string?)runtime?.Element("appDomainManagerType")?.Attribute("value") == "VirtualDesktop.LibraryOverrides.Manager";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or XmlException or ArgumentException)
        { return false; }
    }

    public async Task InstallIntegrationAsync()
    {
        var setup = Path.Combine(AppContext.BaseDirectory, "Install Streamer Helper.exe");
        if (!File.Exists(setup))
            throw new FileNotFoundException(Localizer.T("Install Streamer Helper.exe is missing. Extract the full VD Unlock archive."));
        using var process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true })
            ?? throw new IOException(Localizer.T("Could not start the Streamer setup."));
        await process.WaitForExitAsync();
        // Setup displays its own errors and returns a nonzero code on cancellation.
        if (process.ExitCode != 0) AppLog.Current.Info($"Streamer setup exited with code {process.ExitCode}.");
    }

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

    public bool IsFoveaSizeApplied(int size)
    {
        try
        {
            var lines = File.ReadAllLines(Log);
            var processes = Process.GetProcessesByName(ProcessName);
            try
            {
                return processes.Any(process => MatchesFoveaSize(lines, process.Id, process.StartTime.ToUniversalTime(), size));
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool MatchesFoveaSize(IEnumerable<string> lines, int processId, DateTime startedUtc, int size)
    {
        string marker = $" [{processId}] Applied fovea size: ";
        var last = lines.LastOrDefault(line => line.Contains(marker, StringComparison.Ordinal) &&
            MatchesProcessStart(line, startedUtc));
        return last?.EndsWith(marker + size + "%.", StringComparison.Ordinal) == true;
    }

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
            if (helper.ExitCode == 2)
                throw new IOException(Localizer.T("Could not restart Streamer. Run Install Streamer Helper.exe and select your Streamer installation."));
            throw new IOException(Localizer.T("Could not restart Streamer. Check that Streamer and the library integration are installed, then try again."));
        }
        AppLog.Current.Info("Streamer restarted.");
    }

    internal static int RestartInstalledStreamer()
    {
        try
        {
            var executable = StreamerLocation.FindInstalledHelperTarget();
            var folder = Path.GetDirectoryName(executable)!;
            var required = new[] { executable, Path.Combine(folder, "VirtualDesktop.LibraryOverrides.dll"), executable + ".config" };
            var missing = required.Where(path => !File.Exists(path)).ToArray();
            if (missing.Length != 0)
            {
                AppLog.Current.Warning("Cannot restart Streamer. Missing files: " + string.Join(", ", missing));
                return 2;
            }
            using (var file = File.OpenRead(executable))
            {
                var hash = Convert.ToHexString(SHA256.HashData(file));
                if (hash is not ("FEAD548C57DF539006E6C97A7A29D46FDE172F756F40D65DBDB77ABAAFA3AEAD" or
                    "0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8"))
                    throw new IOException("The Streamer executable no longer matches a verified build.");
            }
            using var current = Process.GetCurrentProcess();
            var sessionId = current.SessionId;
            var previous = new HashSet<int>();
            var processes = Process.GetProcessesByName(ProcessName);
            try
            {
                foreach (var process in processes)
                {
                    if (!IsInstalledStreamer(process, sessionId, executable)) continue;
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
                if (TryFindReplacement(sessionId, executable, previous, out var readyPid))
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
                    using var started = Process.Start(new ProcessStartInfo(executable)
                    { WorkingDirectory = folder, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
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

    private static bool IsInstalledStreamer(Process process, int sessionId, string executable) =>
        !process.HasExited && process.SessionId == sessionId &&
        string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);

    private static bool TryFindReplacement(int sessionId, string executable, HashSet<int> previous, out int? readyPid)
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
                    if (previous.Contains(process.Id) || !IsInstalledStreamer(process, sessionId, executable)) continue;
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
