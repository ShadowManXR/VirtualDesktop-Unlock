using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VirtualDesktop.LibraryOverrides;

class LaunchPresetHookTest
{
    [STAThread] static int Main(string[] args){try{
        string dir=Path.GetFullPath(args[1]);Directory.CreateDirectory(dir);
        string config=Path.Combine(dir,"LaunchPresets.xml");File.WriteAllText(config,"<LaunchPresets version=\"1\"><Game platform=\"Steam\" appId=\"2981220\" fps=\"144\" quality=\"0\"/></LaunchPresets>");Environment.SetEnvironmentVariable("VD_LAUNCH_PRESETS_FILE",config);
        var app=new Application();var a=Assembly.LoadFrom(args[0]);
        a.ManifestModule.ResolveField(0x04014843).SetValue(null,dir);a.ManifestModule.ResolveField(0x04014844).SetValue(null,dir);
        LaunchPresetRuntime.Install(a,Console.WriteLine);
        int calls=0;object captured=null;
        // Substitute only the continuation after validating/building the real one.
        // This process never invokes VD's game-launch state machine.
        typeof(LaunchPresetRuntime).GetField("original",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,new Func<object,Task>(delegate(object id){calls++;captured=id;return Task.FromResult(true);}));
        var game=a.GetType("VirtualDesktop.Interfaces.GameID");var platform=a.GetType("VirtualDesktop.Interfaces.GamePlatform");var entry=(MethodInfo)a.ManifestModule.ResolveMethod(0x060001E8);
        var other=Activator.CreateInstance(game,new[]{Enum.ToObject(platform,1),(object)(ulong)123456789});
        ((Task)entry.Invoke(null,new[]{other})).GetAwaiter().GetResult();
        if(calls!=1||!other.Equals(captured))throw new Exception("Unrelated launch lost identity or continuation.");Console.WriteLine("PASS actual detour preserves unrelated game's ABI and original continuation");
        File.WriteAllText(config,"<LaunchPresets version=\"1\"><Game platform=\"Steam\" appId=\"123456789\" fps=\"96\" quality=\"2\"/></LaunchPresets>");
        var task=(Task)entry.Invoke(null,new[]{other});
        var timer=new DispatcherTimer();timer.Interval=TimeSpan.FromMilliseconds(20);var deadline=DateTime.UtcNow.AddSeconds(10);timer.Tick+=delegate{if(task.IsCompleted||DateTime.UtcNow>deadline){timer.Stop();app.Shutdown();}};timer.Start();app.Run();
        if(!task.IsFaulted||calls!=1)throw new Exception("Configured launch was not held for initialized headset settings.");
        Console.WriteLine("PASS actual detour reloads a non-Forefront game preset and blocks launch when headset settings are unavailable: "+task.Exception.GetBaseException().Message);
        Console.WriteLine("No installed helper, live settings, or game process changed.");return 0;
    }catch(Exception e){Console.WriteLine(e);return 1;}}
}
