using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace VirtualDesktop.LibraryOverrides
{
    internal static class VentureWebp
    {
        internal static Bitmap Read(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 12 || file.Length > 16 * 1024 * 1024) return null;
            byte[] header = new byte[12];
            using (var input = file.OpenRead())
                if (input.Read(header, 0, header.Length) != header.Length) return null;
            if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F' ||
                header[8] != 'W' || header[9] != 'E' || header[10] != 'B' || header[11] != 'P') return null;

            string temporary = Path.Combine(Path.GetTempPath(), "VDUnlock-WebP-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string decoder = Path.Combine(temporary, "dwebp.exe");
            string decoded = Path.Combine(temporary, "cover.png");
            try
            {
                using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("VDUnlock.dwebp.exe"))
                using (var output = new FileStream(decoder, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);
                using (var process = Process.Start(new ProcessStartInfo(decoder, "-quiet \"" + path + "\" -o \"" + decoded + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = temporary
                }))
                {
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        process.WaitForExit();
                        return null;
                    }
                    if (process.ExitCode != 0) return null;
                }
                using (var image = Image.FromFile(decoded))
                    return new Bitmap(image);
            }
            finally
            {
                try { File.Delete(decoder); File.Delete(decoded); Directory.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
