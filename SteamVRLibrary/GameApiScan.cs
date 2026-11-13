using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamVRLibrary;

internal sealed record GameApiSession(string Api, string? Version, string VersionLabel, string Runtime,
    string? RuntimeVersion, DateTimeOffset Started, string Source);

internal sealed record GameApiDetails(string[] Advertised, GameApiSession? LastObserved)
{
    internal string Describe(Game game)
    {
        var lines = new List<string>
        {
            game.Name, "",
            Localizer.T("Steam advertises: {0}", Advertised.Length == 0 ? Localizer.T("Unknown") : string.Join(" + ", Advertised))
        };
        if (LastObserved is not { } session)
        {
            lines.Add(Localizer.T("No matching VR session was found in the available logs."));
            return string.Join(Environment.NewLine, lines);
        }
        lines.Add(Localizer.T("Last observed API: {0}", session.Api));
        lines.Add(Localizer.T(session.VersionLabel, session.Version ?? Localizer.T("Unknown")));
        lines.Add(Localizer.T("Runtime: {0}", session.RuntimeVersion == null ? session.Runtime : $"{session.Runtime} {session.RuntimeVersion}"));
        lines.Add(Localizer.T("Observed: {0}", session.Started.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)));
        lines.Add(Localizer.T("Source: {0}", session.Source));
        return string.Join(Environment.NewLine, lines);
    }
}

internal static class GameApiScan
{
    private const string ApiVersion = "API version: {0}";
    private const string OpenVrVersion = "OpenVR SDK or interface: {0}";
    private static readonly Regex VdxrLine = new(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{4}): (?<text>.*)$");
    private static readonly Regex VdxrApplication = new(@"^Application: .* \((?<exe>[^()]+\.exe)\); Engine:", RegexOptions.IgnoreCase);
    private static readonly Regex SteamLine = new(@"^(?<time>\w{3} \w{3} +\d{1,2} \d{4} \d{2}:\d{2}:\d{2}\.\d{3}) \[\w+\] - (?<text>.*)$");
    private static readonly Regex SteamStart = new(@"^.+ (?<version>\d+(?:\.\d+){1,3}) startup with PID=\d+,");
    private static readonly Regex SteamAppKey = new(@"^App key after connect message:\s*steam\.app\.(?<id>\d+)$");
    private static readonly Regex XrVersion = new(@"^(?:\[OpenXR\] )?(?:Application requests API version |OpenXR API version[: ]+)(?<version>\d+\.\d+(?:\.\d+)?)");
    private static readonly Regex SdkVersion = new(@"^OpenVR SDK version (?<version>\d+\.\d+(?:\.\d+)?)");
    private static readonly Regex SystemInterface = new(@"^.*(?:GetGenericInterface|Requested interface).*?\b(?<version>IVRSystem_\d+)\b");
    private static readonly Regex VdxrVersion = new(@"^VirtualDesktopOpenXR - v(?<version>\d+(?:\.\d+){1,3})");

    internal static GameApiDetails Scan(Game game, IEnumerable<Game> library, string steamRoot, string vdxrLog)
    {
        var advertised = new HashSet<string>();
        try
        {
            SteamMetadata.ReadVrIds(Path.Combine(steamRoot, "appcache", "appinfo.vdf"), [game.Id],
                apiFound: (_, flag) =>
                {
                    if (flag == "openvrsupport") advertised.Add("OpenVR");
                    if (flag == "openxrsupport") advertised.Add("OpenXR");
                });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            advertised.Clear();
            AppLog.Current.Error("Could not read Steam VR API metadata", error);
        }

        var executables = ExecutableNames(game);
        var sessions = new List<GameApiSession>();
        var sharedNames = new Lazy<HashSet<string>>(() => library.Where(other => other.Id != game.Id)
            .SelectMany(ExecutableNames).Where(executables.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase));
        bool UniqueExecutable(string name) => executables.Contains(name) && !sharedNames.Value.Contains(name);

        sessions.AddRange(ReadVdxr(ReadLog(vdxrLog), UniqueExecutable));
        var logs = Path.Combine(steamRoot, "logs");
        try
        {
            if (Directory.Exists(logs))
            {
                foreach (var path in Directory.EnumerateFiles(logs, "vrclient_*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(path)["vrclient_".Length..];
                    if (name.EndsWith(".previous", StringComparison.OrdinalIgnoreCase)) name = name[..^".previous".Length];
                    name += ".exe";
                    if (executables.Contains(name))
                        sessions.AddRange(ReadSteam(ReadLog(path), game.Id, UniqueExecutable(name)));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Current.Error("Could not enumerate SteamVR client logs", error);
        }
        return new GameApiDetails(advertised.Order().ToArray(), sessions
            .MaxBy(session => (session.Started, session.Version != null)));
    }

    private static HashSet<string> ExecutableNames(Game game)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (game.Installed)
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 12 };
                foreach (var path in Directory.EnumerateFiles(game.InstallDirectory, "*.exe", options))
                    names.Add(Path.GetFileName(path));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Current.Error("Could not scan executables for Steam app " + game.Id, error);
        }
        return names;
    }

    private static IEnumerable<string> ReadLog(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool truncated = file.Length > 1024 * 1024;
            if (truncated) file.Seek(-1024 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(file);
            if (truncated) reader.ReadLine();
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Current.Error("Could not read VR runtime log " + Path.GetFileName(path), error);
            return [];
        }
    }

    internal static IEnumerable<GameApiSession> ReadVdxr(IEnumerable<string> lines, Func<string, bool> matchesGame)
    {
        string? runtimeVersion = null;
        GameApiSession? current = null;
        bool usingRuntime = false;
        foreach (var line in lines)
        {
            var entry = VdxrLine.Match(line);
            if (!entry.Success) continue;
            var message = entry.Groups["text"].Value;
            var header = VdxrVersion.Match(message);
            var app = VdxrApplication.Match(message);
            if (header.Success || message.StartsWith("Application: ", StringComparison.Ordinal))
            {
                if (current != null && usingRuntime) yield return current;
                current = null;
                usingRuntime = false;
                if (header.Success) runtimeVersion = header.Groups["version"].Value;
                var time = entry.Groups["time"].Value.Insert(23, ":");
                if (app.Success && matchesGame(app.Groups["exe"].Value) &&
                    DateTimeOffset.TryParseExact(time, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var started))
                    current = new GameApiSession("OpenXR", null, ApiVersion, "VDXR", runtimeVersion, started, "OpenXR.log");
            }
            else if (current != null)
            {
                if (message == "Using Virtual Desktop runtime") usingRuntime = true;
                var version = XrVersion.Match(message);
                if (version.Success) current = current with { Version = version.Groups["version"].Value };
            }
        }
        if (current != null && usingRuntime) yield return current;
    }

    internal static IEnumerable<GameApiSession> ReadSteam(IEnumerable<string> lines, string appId, bool uniqueExecutable)
    {
        DateTimeOffset started = default;
        string? runtimeVersion = null, api = null, version = null, loggedId = null;
        bool connected = false;
        GameApiSession? Session() => connected && api != null && (loggedId == null ? uniqueExecutable : loggedId == appId)
            ? new GameApiSession(api, version, api == "OpenXR" ? ApiVersion : OpenVrVersion, "SteamVR", runtimeVersion,
                started, "vrclient_*.txt")
            : null;
        foreach (var line in lines)
        {
            var entry = SteamLine.Match(line);
            if (!entry.Success) continue;
            var message = entry.Groups["text"].Value;
            var start = SteamStart.Match(message);
            if (start.Success)
            {
                if (Session() is { } session) yield return session;
                connected = false;
                api = version = loggedId = runtimeVersion = null;
                if (DateTimeOffset.TryParseExact(entry.Groups["time"].Value, "ddd MMM d yyyy HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowInnerWhite | DateTimeStyles.AssumeLocal, out started))
                    runtimeVersion = start.Groups["version"].Value;
                continue;
            }
            if (runtimeVersion == null) continue;
            if (message == "vrclient type=VRApplication_Scene") api = "OpenVR";
            if (message.StartsWith("vrclient type=VRApplication_OpenXR", StringComparison.Ordinal)) api = "OpenXR";
            if (message == "Received success response from vrcompositor connect" ||
                message == "Received success response from vrserver connect") connected = true;
            var key = SteamAppKey.Match(message);
            if (key.Success) loggedId = key.Groups["id"].Value;
            var detectedVersion = api == "OpenXR" ? XrVersion.Match(message) : SdkVersion.Match(message);
            if (!detectedVersion.Success && api == "OpenVR" && version == null) detectedVersion = SystemInterface.Match(message);
            if (api != null && detectedVersion.Success) version = detectedVersion.Groups["version"].Value;
        }
        if (Session() is { } last) yield return last;
    }
}
