using System.Security.Cryptography;
using System.Text.Json;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

public sealed class LaunchPresetStore(string path)
{
    private string? revision;
    private string? Revision() => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
    public LaunchPresetConfiguration Load() { var config=LaunchPresetConfiguration.Read(path); revision=Revision(); return config; }
    public void Save(LaunchPresetConfiguration config)
    {
        if(Revision()!=revision)throw new IOException(Localizer.T("Presets changed in another window. Reopen Launch Presets and try again."));
        config.Write(path);revision=Revision();
    }
    public static List<Game> CachedGames(string cachePath,IEnumerable<Game> knownGames)
    {
        if(!File.Exists(cachePath))return [];
        using var doc=JsonDocument.Parse(File.ReadAllBytes(cachePath));
        if(!doc.RootElement.TryGetProperty("SteamProductInfos",out var infos))return [];
        var entries=infos.EnumerateObject().Where(p=>uint.TryParse(p.Name,out var id)&&id!=0).ToArray();
        var known=knownGames.DistinctBy(g=>g.Id).ToDictionary(g=>g.Id);
        var names=new Dictionary<string,string>();
        var steamRoot=Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null) as string
            ?? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam","InstallPath",null) as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");
        try{SteamMetadata.ReadVrIds(Path.Combine(steamRoot,"appcache","appinfo.vdf"),entries.Select(p=>p.Name).ToHashSet(),names);}catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException){}
        return entries.Select(p=>known.TryGetValue(p.Name,out var game)?game:new Game(p.Name,names.GetValueOrDefault(p.Name,Localizer.T("Steam app {0}",p.Name)),""))
            .OrderBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
