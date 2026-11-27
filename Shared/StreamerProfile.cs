using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

namespace VirtualDesktop.Unlock
{
    public sealed class StreamerProfile
    {
        public string Sha256, Version;
        public Guid Mvid;
        public readonly Dictionary<int,int> Tokens = new Dictionary<int,int>();
        private static readonly object gate = new object();
        private static readonly Dictionary<Module,StreamerProfile> profiles = new Dictionary<Module,StreamerProfile>();
        public const string Original13425 = "0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8";
        public const string Original13423 = "FEAD548C57DF539006E6C97A7A29D46FDE172F756F40D65DBDB77ABAAFA3AEAD";
        public static string Hash(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        public void Write(string path)
        {
            using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true }))
            {
                writer.WriteStartElement("StreamerCompatibility"); writer.WriteAttributeString("format", "1"); writer.WriteAttributeString("family", "13425");
                writer.WriteAttributeString("sha256", Sha256); writer.WriteAttributeString("mvid", Mvid.ToString()); writer.WriteAttributeString("version", Version);
                foreach (var pair in Tokens.OrderBy(p => p.Key))
                {
                    writer.WriteStartElement("Symbol"); writer.WriteAttributeString("legacy", pair.Key.ToString("X8")); writer.WriteAttributeString("actual", pair.Value.ToString("X8")); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }
        public static StreamerProfile Read(string path)
        {
            var result = new StreamerProfile();
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
            {
                reader.MoveToContent(); if (reader.Name != "StreamerCompatibility" || reader.GetAttribute("format") != "1" || reader.GetAttribute("family") != "13425") throw new InvalidDataException("Invalid Streamer compatibility profile.");
                result.Sha256 = reader.GetAttribute("sha256"); result.Version = reader.GetAttribute("version"); result.Mvid = Guid.Parse(reader.GetAttribute("mvid"));
                if (result.Sha256 == null || result.Sha256.Length != 64 || !result.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid Streamer profile fingerprint.");
                while (reader.Read()) if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name != "Symbol" || reader.Depth != 1 || result.Tokens.Count >= 256) throw new InvalidDataException("Invalid Streamer symbol mapping.");
                    int legacy = Int32.Parse(reader.GetAttribute("legacy"), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int actual = Int32.Parse(reader.GetAttribute("actual"), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if ((legacy & unchecked((int)0xFF000000)) != (actual & unchecked((int)0xFF000000)) || result.Tokens.ContainsKey(legacy)) throw new InvalidDataException("Invalid or duplicate Streamer symbol mapping.");
                    result.Tokens.Add(legacy, actual);
                }
            }
            if (result.Tokens.Count != StreamerSymbols.Required.Length || StreamerSymbols.Required.Any(t => !result.Tokens.ContainsKey(t)) || result.Tokens.Values.Distinct().Count() != result.Tokens.Count) throw new InvalidDataException("The Streamer profile does not resolve every required symbol uniquely.");
            return result;
        }
        public static bool IsVerifiedFile(string path)
        {
            string hash = Hash(path); if (hash == Original13425 || hash == Original13423) return true;
            string profilePath = Environment.GetEnvironmentVariable("VD_COMPATIBILITY_PROFILE") ?? Path.Combine(Path.GetDirectoryName(path), StreamerSymbols.ProfileFileName);
            if (!File.Exists(profilePath)) return false;
            return String.Equals(Read(profilePath).Sha256, hash, StringComparison.OrdinalIgnoreCase);
        }
        public static void Verify(Assembly assembly) { Get(assembly.ManifestModule); }
        public static bool IsModern(Assembly assembly) { return Get(assembly.ManifestModule).Sha256 != Original13423; }
        private static StreamerProfile Get(Module module)
        {
            lock (gate)
            {
                StreamerProfile result; if (profiles.TryGetValue(module, out result)) return result;
                string hash = Hash(module.Assembly.Location);
                if (module.Assembly.GetName().Name != "VirtualDesktop.Streamer") throw new NotSupportedException("Unexpected Streamer assembly.");
                string path = Environment.GetEnvironmentVariable("VD_COMPATIBILITY_PROFILE") ?? Path.Combine(Path.GetDirectoryName(module.Assembly.Location), StreamerSymbols.ProfileFileName);
                if (File.Exists(path))
                {
                    result = Read(path);
                    if (!String.Equals(result.Sha256, hash, StringComparison.OrdinalIgnoreCase) || result.Mvid != module.ModuleVersionId) throw new NotSupportedException("Streamer changed since analysis. Run Virtual Desktop Unlock Installer.exe again.");
                    List<SymbolSignature> baseline;
                    using (var input = typeof(StreamerProfile).Assembly.GetManifestResourceStream(StreamerSymbols.BaselineResource))
                    {
                        if (input == null) throw new InvalidDataException("Compatibility signatures are missing.");
                        baseline = CompatibilitySignatures.Read(input);
                    }
                    var signatures = new CompatibilitySignatures();
                    foreach (var pair in result.Tokens)
                    {
                        var member = module.ResolveMember(pair.Value); string key = signatures.Key(member), body = signatures.Body(member);
                        if (!baseline.Any(s => s.LegacyToken == pair.Key && s.Key == key && s.Body == body)) throw new NotSupportedException("Streamer compatibility validation failed for " + pair.Key.ToString("X8") + ". Run the installer again.");
                    }
                }
                else if (hash == Original13425 || hash == Original13423)
                {
                    result = new StreamerProfile { Sha256 = hash, Version = module.Assembly.GetName().Version.ToString(), Mvid = module.ModuleVersionId };
                }
                else throw new NotSupportedException("This Streamer build requires compatibility analysis. Run Virtual Desktop Unlock Installer.exe.");
                if(result.Tokens.Count!=0 && FieldOwner(module,result.Tokens[0x04014CFD]) != module.ResolveMethod(result.Tokens[0x06021474]).GetParameters()[0].ParameterType.GetElementType())
                    throw new NotSupportedException("Invalid slice writer relationship.");
                profiles.Add(module, result); return result;
            }
        }
        private static Type FieldOwner(Module module,int token) { return module.ResolveField(token).DeclaringType; }
        public static int Token(Module module, int legacy)
        {
            var profile = Get(module); if (profile.Tokens.Count == 0) return legacy;
            int token; if (!profile.Tokens.TryGetValue(legacy, out token)) throw new NotSupportedException("Unresolved Streamer symbol " + legacy.ToString("X8")); return token;
        }
        public static FieldInfo Field(Module module, int legacy) { return module.ResolveField(Token(module, legacy)); }
        public static MethodBase Method(Module module, int legacy) { return module.ResolveMethod(Token(module, legacy)); }
        public static Type Type(Module module, int legacy) { return module.ResolveType(Token(module, legacy)); }
    }
}
