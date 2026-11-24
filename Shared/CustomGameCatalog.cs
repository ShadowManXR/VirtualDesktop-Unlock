using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;

namespace VirtualDesktop.Unlock
{
    public sealed class CustomGameEntry
    {
        public uint Id;
        public string Name = "", Executable = "", Cover = "";
        public bool Selected;

        public ulong DashboardId { get { return 0x5644554300000000UL | Id; } }
    }

    public static class CustomGameCatalog
    {
        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Virtual Desktop Unlock", "CustomGames.xml"); }
        }

        public static List<CustomGameEntry> Read(string path)
        {
            var games = new List<CustomGameEntry>();
            if (!File.Exists(path)) return games;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings)) document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.Name != "CustomGames" || root.GetAttribute("version") != "1")
                throw new InvalidDataException("Unsupported custom game catalog.");
            var ids = new HashSet<uint>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in root.ChildNodes)
            {
                var element = node as XmlElement;
                if (element == null || element.Name != "Game") continue;
                uint id;
                bool selected;
                string exe = element.GetAttribute("executable");
                string name = element.GetAttribute("name");
                string cover = element.GetAttribute("cover");
                if (!UInt32.TryParse(element.GetAttribute("id"), NumberStyles.None, CultureInfo.InvariantCulture, out id) ||
                    id < 0x80000000 || !ids.Add(id) || String.IsNullOrWhiteSpace(name) ||
                    !Path.IsPathRooted(exe) || !String.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    !paths.Add(Path.GetFullPath(exe)) || !Path.IsPathRooted(cover) ||
                    !Boolean.TryParse(element.GetAttribute("selected"), out selected))
                    throw new InvalidDataException("Invalid custom game entry.");
                games.Add(new CustomGameEntry { Id = id, Name = name, Executable = Path.GetFullPath(exe), Cover = cover, Selected = selected });
            }
            return games;
        }

        public static string Fingerprint(string path)
        {
            if (!File.Exists(path)) return "";
            using (var hash = SHA256.Create())
            using (var file = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        }

        public static void Save(string path, IEnumerable<CustomGameEntry> games, string expectedFingerprint)
        {
            if (Fingerprint(path) != expectedFingerprint) throw new IOException("The custom game catalog changed. Refresh and try again.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("CustomGames");
                    writer.WriteAttributeString("version", "1");
                    foreach (var game in games.OrderBy(game => game.Id))
                    {
                        writer.WriteStartElement("Game");
                        writer.WriteAttributeString("id", game.Id.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("name", game.Name);
                        writer.WriteAttributeString("executable", game.Executable);
                        writer.WriteAttributeString("cover", game.Cover);
                        writer.WriteAttributeString("selected", XmlConvert.ToString(game.Selected));
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                Read(temporary);
                if (Fingerprint(path) != expectedFingerprint) throw new IOException("The custom game catalog changed. Refresh and try again.");
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
