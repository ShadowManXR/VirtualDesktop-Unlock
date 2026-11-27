using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using VirtualDesktop.Unlock;

internal static class CompatibilityTool
{
    static int Main(string[] args)
    {
        try
        {
            if(args[0]=="installer-analysis") { var installer=System.Reflection.Assembly.LoadFile(Path.GetFullPath(args[1])); var method=installer.GetType("StreamerSetup").GetMethod("Analyze",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic); var report=(string)method.Invoke(null,new object[]{Path.GetFullPath(args[2]),false});File.WriteAllText(args[3],report);Console.WriteLine(report.Split('\n').Last());return 0; }
            if(args[0]=="payload") { var assembly=System.Reflection.Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(args[1])); using(var source=assembly.GetManifestResourceStream("Application.zip")) using(var output=File.Create(args[2])) source.CopyTo(output); return 0; }
            if(args[0]=="verify") { Environment.SetEnvironmentVariable("VD_COMPATIBILITY_PROFILE",Path.GetFullPath(args[2])); StreamerProfile.Verify(CompatibilityAnalysis.Open(args[1])); Console.WriteLine("PASS runtime profile verification");return 0; }
            if (args[0] == "export")
            {
                var assembly = CompatibilityAnalysis.Open(args[1]); var signatures = new CompatibilitySignatures();
                var list = StreamerSymbols.Required.Select(t => signatures.Describe(assembly.ManifestModule, t)).ToList();
                CompatibilitySignatures.Write(args[2], list); Console.WriteLine("Exported " + list.Count + " baseline symbols."); return 0;
            }
            if (args[0] == "variant")
            {
                var assembly = CompatibilityAnalysis.Open(args[1]); var signatures = new CompatibilitySignatures();

                var map = new JavaScriptSerializer().Deserialize<List<Dictionary<string,object>>>(File.ReadAllText(args[3]));
                List<SymbolSignature> list; using (var input = File.OpenRead(args[2])) list = CompatibilitySignatures.Read(input);
                foreach (int legacy in StreamerSymbols.Required)
                {
                    var entry = map.Single(e => (string)e["Old"] == legacy.ToString("X8")); if(entry["New"]==null) continue; int actual = Convert.ToInt32((string)entry["New"], 16);
                    var description = signatures.Describe(assembly.ManifestModule, actual); description.LegacyToken = legacy;
                    if (!list.Any(s => s.LegacyToken == legacy && s.Key == description.Key && s.Body == description.Body)) list.Add(description);
                }
                CompatibilitySignatures.Write(args[2], list); Console.WriteLine("Stored " + list.Count + " signature variants."); return 0;
            }
            List<SymbolSignature> baseline; using (var input = File.OpenRead(args[2])) baseline = CompatibilitySignatures.Read(input);
            var result = CompatibilityAnalysis.Analyze(args[1], baseline);
            Console.WriteLine("Version: " + result.Version + "; SHA256: " + result.Sha256 + "; MVID: " + result.Mvid);
            foreach (string message in result.Messages) Console.WriteLine(message);
            File.WriteAllText(args[3], new JavaScriptSerializer().Serialize(result.Tokens.ToDictionary(p => p.Key.ToString("X8"), p => p.Value.ToString("X8"))));
            if(result.Compatible) { var profile=new StreamerProfile { Sha256=result.Sha256, Version=result.Version, Mvid=result.Mvid }; foreach(var pair in result.Tokens) profile.Tokens.Add(pair.Key,pair.Value); profile.Write(args[3]+".xml"); }
            return result.Compatible ? 0 : 2;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
