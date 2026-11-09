using System;
using System.IO;

namespace VirtualDesktop.Unlock
{
    public sealed class VenturePreferences
    {
        public static string LauncherPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Venture", "venture.exe"); }
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Virtual Desktop Unlock", "Venture.enabled"); }
        }

        private readonly string path;

        public VenturePreferences(string path)
        {
            this.path = path;
        }

        public bool Enabled { get { return File.Exists(path); } }

        public void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
        }
    }
}
