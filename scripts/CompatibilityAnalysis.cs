using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using VirtualDesktop.Unlock;

internal sealed class CompatibilityAnalysis
{
    internal readonly Dictionary<int,int> Tokens = new Dictionary<int,int>();
    internal readonly List<string> Messages = new List<string>();
    internal string Sha256, Version;
    internal Guid Mvid;
    internal bool Compatible;
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly object reflectionGate = new object();
    private static string dependencyDirectory;

    internal static Assembly Open(string path)
    {
        // This worker/installer inspects one target per process. Reflection-only
        // loading never executes its module initializer or target methods.
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += Resolve;
        dependencyDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        return Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(path));
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var loaded = AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies().FirstOrDefault(a => a.FullName == args.Name);
        if (loaded != null) return loaded;
        try { return Assembly.ReflectionOnlyLoad(args.Name); }
        catch (FileNotFoundException)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            string framework = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            foreach (string folder in new[] { dependencyDirectory, framework, Path.Combine(framework, "Facades"), Path.Combine(framework, "WPF") })
            {
                string file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.ReflectionOnlyLoadFrom(file);
            }
            throw;
        }
    }
    internal static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    internal static CompatibilityAnalysis Analyze(string path, IList<SymbolSignature> baseline)
    {
        lock (reflectionGate)
        {
            var result = new CompatibilityAnalysis { Sha256 = Hash(path) };
            var assembly = Open(path); result.Version = assembly.GetName().Version.ToString(); result.Mvid = assembly.ManifestModule.ModuleVersionId;
            if (assembly.GetName().Name != "VirtualDesktop.Streamer") throw new InvalidDataException("Select the Virtual Desktop Streamer executable.");
            PortableExecutableKinds kind; ImageFileMachine machine; assembly.ManifestModule.GetPEKind(out kind, out machine);
            if (machine != ImageFileMachine.AMD64) throw new NotSupportedException("The Streamer integration requires the x64 build.");
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { Resolve(null, new ResolveEventArgs(reference.FullName)); result.Messages.Add("Dependency available: " + reference.Name + " " + reference.Version); }
                catch (Exception error) { throw new FileNotFoundException("A Streamer dependency is unavailable: " + reference.FullName, error); }
            }
            var nativeDependencies=NativeDependencies.Inspect(path).ToList();
            result.Messages.AddRange(nativeDependencies);
            result.Messages.Add("Native PE import inspection complete: "+nativeDependencies.Count+" static dependencies.");
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { throw new InvalidDataException("Streamer metadata dependencies could not be resolved: " + String.Join("; ", error.LoaderExceptions.Select(e => e.Message).Distinct()), error); }
            var signatures = new CompatibilitySignatures();
            var wanted = new HashSet<string>(baseline.Select(s => s.Key));
            var candidates = new Dictionary<string,List<MemberInfo>>();
            foreach (var type in types)
            {
                var members = new List<MemberInfo> { type };
                members.AddRange(type.GetFields(All)); members.AddRange(type.GetMethods(All)); members.AddRange(type.GetConstructors(All));
                foreach (var member in members)
                {
                    string key = signatures.Key(member); if (!wanted.Contains(key)) continue;
                    List<MemberInfo> list; if (!candidates.TryGetValue(key, out list)) { list = new List<MemberInfo>(); candidates.Add(key, list); }
                    list.Add(member);
                }
            }
            foreach (int legacy in StreamerSymbols.Required.OrderByDescending(t=>t))
            {
                var matches = new Dictionary<int,MemberInfo>();
                foreach (var expected in baseline.Where(s => s.LegacyToken == legacy))
                {
                    List<MemberInfo> list; if (!candidates.TryGetValue(expected.Key, out list)) continue;
                    foreach (var member in list)
                    {
                        if (signatures.Body(member) == expected.Body) {
                            // The slice byte count must belong to the record written by the resolved slice writer.
                            if(legacy==0x04014CFD) { int writer; if(!result.Tokens.TryGetValue(0x06021474,out writer)) continue;
                                var record=assembly.ManifestModule.ResolveMethod(writer).GetParameters()[0].ParameterType.GetElementType();
                                if(member.DeclaringType!=record) continue; }
                            matches[member.MetadataToken] = member;
                        }
                    }
                }
                if (matches.Count != 1) result.Messages.Add("Unresolved symbol " + legacy.ToString("X8") + ": " + matches.Count + " validated matches.");
                else result.Tokens.Add(legacy, matches.Keys.Single());

            }
            result.Compatible = result.Tokens.Count == StreamerSymbols.Required.Length;
            result.Messages.Add("Resolved " + result.Tokens.Count + " of " + StreamerSymbols.Required.Length + " required integration symbols.");
            if (Hash(path) != result.Sha256) throw new IOException("Streamer changed while it was being analyzed. Run analysis again.");
            return result;
        }
    }
}
