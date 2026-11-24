using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VirtualDesktop.LibraryOverrides
{
    internal static class CustomGameLauncher
    {
        // The supervisor remains alive through bootstrapper handoffs. Closing it
        // closes the job handle, which terminates the game's remaining children.
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) return 2;
                string executable = Encoding.Unicode.GetString(Convert.FromBase64String(args[0]));
                using (var game = VentureGameProcess.Start(executable, ""))
                {
                    while (game.HasRunningProcesses) Thread.Sleep(200);
                    game.Process.WaitForExit();
                    return game.Process.ExitCode;
                }
            }
            catch (Exception error)
            {
                try
                {
                    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Virtual Desktop Unlock", "Logs");
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, "CustomGameLauncher.log"), DateTimeOffset.Now.ToString("o") + " " + error + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return 1;
            }
        }

        internal static string Extract()
        {
            byte[] bytes;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("VDUnlock.CustomGameLauncher.exe"))
            using (var memory = new MemoryStream())
            {
                if (resource == null) throw new FileNotFoundException("Custom game launcher resource is missing.");
                resource.CopyTo(memory);
                bytes = memory.ToArray();
            }
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Virtual Desktop Unlock", "CustomLauncher", hash);
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "CustomGameLauncher.exe");
            if (File.Exists(executable))
            {
                using (var sha = SHA256.Create())
                using (var input = File.OpenRead(executable))
                    if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") != hash)
                        throw new InvalidDataException("Custom game launcher checksum mismatch.");
                return executable;
            }
            string temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, executable); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return executable;
        }
    }
}
