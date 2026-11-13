using System;
using System.Globalization;
using System.IO;

namespace VirtualDesktop.Unlock
{
    public sealed class FoveaSizePreferences
    {
        public const int Default = 25;
        public const int Minimum = 5;
        public const int Maximum = 40;

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Virtual Desktop Unlock", "FoveaSize.txt"); }
        }

        private readonly string path;

        public FoveaSizePreferences(string path) { this.path = path; }

        public int? Read()
        {
            if (!File.Exists(path)) return null;
            int value;
            if (!Int32.TryParse(File.ReadAllText(path), NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value < Minimum || value > Maximum)
                throw new InvalidDataException("Invalid fovea size setting.");
            return value;
        }

        public void Save(int value)
        {
            if (value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException("value");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, value.ToString(CultureInfo.InvariantCulture));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { File.Delete(temporary); }
        }
    }
}
