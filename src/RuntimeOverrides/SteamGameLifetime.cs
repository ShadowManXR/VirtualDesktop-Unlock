using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VirtualDesktop.LibraryOverrides
{
    public static class SteamGameLifetime
    {
        private static readonly Regex pathValue=new Regex("\"path\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"",RegexOptions.IgnoreCase);
        private static string Unescape(string value){return value.Replace("\\\\","\\").Replace("\\\"","\"");}
        public static string InstallDirectory(uint id,string steamRoot=null)
        {
            string steam=steamRoot ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam","InstallPath",null) as string
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");
            var libraries=new HashSet<string>(StringComparer.OrdinalIgnoreCase){steam};
            string file=Path.Combine(steam,"steamapps","libraryfolders.vdf");
            if(File.Exists(file))foreach(Match m in pathValue.Matches(File.ReadAllText(file)))libraries.Add(Unescape(m.Groups[1].Value));
            foreach(string library in libraries){
                string manifest=Path.Combine(library,"steamapps","appmanifest_"+id+".acf");if(!File.Exists(manifest))continue;
                var match=Regex.Match(File.ReadAllText(manifest),"\"installdir\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"",RegexOptions.IgnoreCase);
                if(!match.Success)continue;
                string common=Path.GetFullPath(Path.Combine(library,"steamapps","common"))+Path.DirectorySeparatorChar;
                string directory=Path.GetFullPath(Path.Combine(common,Unescape(match.Groups[1].Value)));
                if(directory.StartsWith(common,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(directory))return directory;
            }
            throw new InvalidOperationException("Could not locate the game's Steam installation for exit recovery.");
        }
        public static HashSet<string> RunningDirectories(IEnumerable<string> directories)
        {
            var roots=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(string directory in directories)roots[directory]=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var running=new HashSet<string>(StringComparer.OrdinalIgnoreCase);if(roots.Count==0)return running;
            // Includes bootstrapper children by their verified executable path,
            // rather than assuming VD's initial PID remains the actual game.
            foreach(var process in Process.GetProcesses())using(process){
                IntPtr handle=OpenProcess(0x1000,false,process.Id);if(handle==IntPtr.Zero)continue;
                try{
                    var image=new StringBuilder(32768);int size=image.Capacity;if(!QueryFullProcessImageName(handle,0,image,ref size))continue;
                    string path=image.ToString(),name=Path.GetFileNameWithoutExtension(path);
                    if(name.StartsWith("UnityCrashHandler",StringComparison.OrdinalIgnoreCase)||name.Equals("CrashReportClient",StringComparison.OrdinalIgnoreCase)||name.EndsWith("_Setup",StringComparison.OrdinalIgnoreCase))continue;
                    foreach(var root in roots)if(path.StartsWith(root.Value,StringComparison.OrdinalIgnoreCase))running.Add(root.Key);
                }finally{CloseHandle(handle);}
            }
            return running;
        }
        [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int id);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder name,ref int size);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    }
}
