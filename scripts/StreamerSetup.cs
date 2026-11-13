using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using VirtualDesktop.Unlock;

internal static class StreamerSetup
{
    private const string Title = "VD Unlock Streamer Helper";
    private const string Installer = "HelperInstaller.ps1";
    private static readonly string[] Payload =
    {
        "VirtualDesktop.LibraryOverrides.dll", "VirtualDesktop.Streamer.exe.config", "manifest.json"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool uninstall = args.Length == 1 && args[0] == "--uninstall";
        try
        {
            var executable = SelectExecutable();
            if (executable == null) return 1;
            string folder = Path.GetDirectoryName(executable);
            if (uninstall && MessageBox.Show("Remove the Streamer helper from this folder?\n\n" + folder,
                Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 1;
            string output = RunInstaller(folder, uninstall);
            MessageBox.Show(output, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static string SelectExecutable()
    {
        string found = StreamerLocation.Find();
        if (found.Length != 0)
        {
            var choice = MessageBox.Show("Use this Virtual Desktop Streamer installation?\n\n" + found +
                "\n\nChoose No to browse for another installation.", Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Yes) return found;
            if (choice == DialogResult.Cancel) return null;
        }
        using (var picker = new OpenFileDialog
        {
            Title = "Select VirtualDesktop.Streamer.exe", FileName = StreamerLocation.ExecutableName,
            Filter = "Virtual Desktop Streamer|VirtualDesktop.Streamer.exe", CheckFileExists = true, RestoreDirectory = true
        })
        {
            if (picker.ShowDialog() != DialogResult.OK) return null;
            string selected = StreamerLocation.ValidatePath(picker.FileName);
            if (selected.Length == 0) throw new IOException("Select VirtualDesktop.Streamer.exe in the Streamer installation folder.");
            return selected;
        }
    }

    private static string RunInstaller(string folder, bool uninstall)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var account in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        string temporary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VDUnlockSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary, security);
        try
        {
            Extract(Installer, Path.Combine(temporary, Installer));
            string payload = Path.Combine(temporary, "runtime-override");
            Directory.CreateDirectory(payload);
            foreach (string name in Payload) Extract(name, Path.Combine(payload, name));

            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(powershell,
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(temporary, Installer) +
                "\" -StreamerDirectory \"" + (folder.EndsWith("\\", StringComparison.Ordinal) ? folder + "\\" : folder) +
                "\"" + (uninstall ? " -Uninstall" : ""))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = temporary
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException((errors.Result + "\n" + output.Result).Trim() +
                        "\n\nSee %ProgramData%\\Virtual Desktop Unlock\\installer.log for details.");
                return output.Result.Trim();
            }
        }
        finally
        {
            try
            {
                foreach (string name in Payload) File.Delete(Path.Combine(temporary, "runtime-override", name));
                string payload = Path.Combine(temporary, "runtime-override");
                if (Directory.Exists(payload)) Directory.Delete(payload);
                File.Delete(Path.Combine(temporary, Installer));
                Directory.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Extract(string name, string destination)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var output = File.Open(destination, FileMode.CreateNew))
        {
            if (input == null) throw new IOException("Missing installer component: " + name);
            input.CopyTo(output);
        }
    }
}
