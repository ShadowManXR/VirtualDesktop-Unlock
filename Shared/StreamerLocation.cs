using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VirtualDesktop.Unlock
{
    internal static class StreamerLocation
    {
        internal const string ExecutableName = "VirtualDesktop.Streamer.exe";
        internal const string ProcessName = "VirtualDesktop.Streamer";
        internal const string RegistryPath = @"SOFTWARE\Virtual Desktop Unlock";

        internal static string Find()
        {
            foreach (string candidate in Candidates())
            {
                string path = ValidatePath(candidate);
                if (path.Length != 0) return path;
            }
            return String.Empty;
        }

        internal static string FindInstalledHelperTarget()
        {
            string path = ValidatePath(RecordedExecutable());
            return path.Length != 0 ? path :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer", ExecutableName);
        }

        private static string RecordedExecutable()
        {
            return ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, key =>
            {
                using (var setup = key.OpenSubKey(RegistryPath))
                    return setup == null ? String.Empty : setup.GetValue("StreamerExecutable") as string ?? String.Empty;
            }, String.Empty);
        }

        internal static string ValidatePath(string candidate)
        {
            if (String.IsNullOrWhiteSpace(candidate)) return String.Empty;
            try
            {
                candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                if (!Path.IsPathRooted(candidate) || candidate.Length < 3 || candidate[1] != ':' ||
                    (candidate[2] != '\\' && candidate[2] != '/')) return String.Empty;
                string path = Path.GetFullPath(candidate);
                return String.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path) ? path : String.Empty;
            }
            catch (ArgumentException) { return String.Empty; }
            catch (NotSupportedException) { return String.Empty; }
            catch (IOException) { return String.Empty; }
        }

        private static IEnumerable<string> Candidates()
        {
            yield return RecordedExecutable();
            var processes = Process.GetProcessesByName(ProcessName);
            try
            {
                using (var current = Process.GetCurrentProcess())
                foreach (var process in processes)
                {
                    string path;
                    try
                    {
                        if (process.SessionId != current.SessionId) continue;
                        var module = process.MainModule;
                        if (module == null) continue;
                        path = module.FileName;
                    }
                    catch (Win32Exception) { continue; }
                    catch (InvalidOperationException) { continue; }
                    yield return path;
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }

            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var locations = ReadRegistry(hive, view, RegisteredInstallations, new List<string>());
                foreach (string path in locations) yield return path;
            }
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                if (!String.IsNullOrEmpty(root)) yield return Path.Combine(root, "Virtual Desktop Streamer", ExecutableName);
            }
        }

        private static T ReadRegistry<T>(RegistryHive hive, RegistryView view, Func<RegistryKey, T> read, T fallback)
        {
            try { using (var key = RegistryKey.OpenBaseKey(hive, view)) return read(key); }
            catch (System.Security.SecurityException) { return fallback; }
            catch (UnauthorizedAccessException) { return fallback; }
            catch (IOException) { return fallback; }
        }

        private static List<string> RegisteredInstallations(RegistryKey key)
        {
            var paths = new List<string>();
            using (var apps = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + ExecutableName))
            {
                var path = apps == null ? null : apps.GetValue(null) as string;
                if (path != null) paths.Add(path);
            }
            using (var entries = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (entries == null) return paths;
                foreach (string name in entries.GetSubKeyNames())
                using (var entry = entries.OpenSubKey(name))
                {
                    if (entry == null || !String.Equals(entry.GetValue("DisplayName") as string, "Virtual Desktop Streamer", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var location = entry.GetValue("InstallLocation") as string;
                    if (!String.IsNullOrWhiteSpace(location))
                        paths.Add(location.Trim().Trim('"').TrimEnd('\\', '/') + "\\" + ExecutableName);
                    var icon = entry.GetValue("DisplayIcon") as string;
                    if (!String.IsNullOrWhiteSpace(icon))
                    {
                        int suffix = icon.LastIndexOf(',');
                        int index;
                        if (suffix >= 0 && Int32.TryParse(icon.Substring(suffix + 1), out index)) icon = icon.Substring(0, suffix);
                        paths.Add(icon);
                    }
                }
            }
            return paths;
        }
    }
}
