namespace SteamVRLibrary;

internal sealed class AppLog(string path)
{
    private const long MaxBytes = 2 * 1024 * 1024;
    internal static AppLog Current { get; set; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Virtual Desktop Unlock", "Logs", "app.log"));

    internal void Info(string message) => Write("INFO", message);
    internal void Warning(string message) => Write("WARN", message);
    internal void Error(string operation, Exception error) =>
        Write("ERROR", operation + Environment.NewLine + error);

    private void Write(string level, string message)
    {
        try
        {
            // The desktop app and its elevated restart command share this log.
            using var mutex = new Mutex(false, @"Local\VirtualDesktopUnlock.AppLog");
            try { if (!mutex.WaitOne(200)) return; }
            catch (AbandonedMutexException) { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length >= MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {level} {message}{Environment.NewLine}");
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }
}
