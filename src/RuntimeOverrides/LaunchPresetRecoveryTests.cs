using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using VirtualDesktop.LibraryOverrides;

class LaunchPresetRecoveryTests
{
    static int checks,fps,quality;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static int[] Current(){return new[]{fps,quality};}
    static void Restore(LaunchPresetRecovery r){r.RestoreReady(Current,delegate(int v){fps=v;},delegate(int v){quality=v;});}
    static LaunchPresetRecovery Recovery(string path){return new LaunchPresetRecovery(path,Console.WriteLine);}
    static int Main(string[] args){try{
        if(args[0]=="--worker"){Thread.Sleep(1500);return 0;}
        if(args[0]=="--bootstrap"){Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--worker"){UseShellExecute=false,CreateNoWindow=true});return 0;}
        string dir=Path.GetFullPath(args[0]);Directory.CreateDirectory(dir);string path=Path.Combine(dir,"recovery.xml");string a=Path.Combine(dir,"game A"),b=Path.Combine(dir,"game B");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        var now=new DateTime(2026,11,8,0,0,0,DateTimeKind.Utc);var r=Recovery(path);fps=90;quality=2;
        r.Begin(1,a,fps,quality,144,0,now);Check(File.Exists(path)&&Recovery(path).Sessions[0].OriginalFps==90,"Original selections are durably saved before preset application");
        fps=144;quality=0;r.Observe(now,delegate{return true;});r.Observe(now.AddSeconds(1),delegate{return false;});Restore(r);Check(fps==144&&quality==0,"A brief bootstrapper handoff does not restore settings");
        r.Observe(now.AddSeconds(2),delegate{return true;});r.Observe(now.AddSeconds(3),delegate{return false;});r.Observe(now.AddSeconds(7),delegate{return false;});Restore(r);
        Check(fps==90&&quality==2&&!File.Exists(path),"Game exit restores both original selections and clears recovery");
        var session=r.Begin(1,a,90,2,144,0,now);fps=120;quality=0;r.Fail(session);Restore(r);Check(fps==120&&quality==2,"Exit preserves manual FPS and restores unchanged quality");
        session=r.Begin(1,a,90,2,144,0,now);fps=144;quality=3;r.Fail(session);Restore(r);Check(fps==90&&quality==3,"Exit preserves manual quality and restores unchanged FPS");
        session=r.Begin(1,a,90,2,144,0,now);fps=90;quality=0;r.Fail(session);Restore(r);Check(fps==90&&quality==2,"Partially applied failed launch is recovered");
        session=r.Begin(1,a,90,2,144,0,now);fps=144;quality=0;r.Fail(session);bool disconnected=false;
        try{r.RestoreReady(delegate{throw new InvalidOperationException("disconnected");},delegate{},delegate{});}catch(InvalidOperationException){disconnected=true;}
        Check(disconnected&&File.Exists(path)&&r.Sessions.Count==1,"Disconnect retains pending recovery");
        r=Recovery(path);Restore(r);Check(fps==90&&quality==2&&!File.Exists(path),"Restart/reconnect resumes pending recovery");
        r.Begin(1,a,90,2,144,0,now);fps=144;quality=0;r.Observe(now,delegate{return true;});r=Recovery(path);r.Observe(now.AddSeconds(10),delegate{return true;});Restore(r);Check(fps==144&&quality==0,"Streamer restart does not restore while the game remains active");
        r.Observe(now.AddSeconds(11),delegate{return false;});r.Observe(now.AddSeconds(15),delegate{return false;});Restore(r);Check(fps==90&&quality==2,"Exit after Streamer restart restores the original baseline");
        r.Begin(1,a,90,2,144,0,now);fps=144;quality=0;r.Observe(now.AddSeconds(30),delegate{return false;});Restore(r);Check(fps==144,"Slow game startup retains preset during startup grace");
        r.Observe(now.AddMinutes(3),delegate{return false;});Restore(r);Check(fps==90&&quality==2,"A game that never starts times out and restores settings");
        var first=r.Begin(1,a,90,2,144,0,now);fps=144;quality=0;var second=r.Begin(2,b,144,0,120,3,now);fps=120;quality=3;
        r.Fail(first);Restore(r);Check(fps==120&&quality==3&&r.Sessions.Count==2,"An older game exiting cannot overwrite a newer game's preset");
        r.Fail(second);Restore(r);Check(fps==90&&quality==2&&r.Sessions.Count==0,"Overlapping game exits unwind to the original baseline");
        first=r.Begin(1,a,90,2,144,0,now);second=r.Begin(2,b,144,0,120,2,now);fps=144;quality=2;r.Fail(first);r.Fail(second);Restore(r);
        Check(fps==144&&quality==2,"A manual selection matching an older preset survives stack recovery");
        string steam=Path.Combine(dir,"Steam"),library=Path.Combine(dir,"Other library ü");Directory.CreateDirectory(Path.Combine(steam,"steamapps"));string common=Path.Combine(library,"steamapps","common","Test Game");Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(steam,"steamapps","libraryfolders.vdf"),"\"libraryfolders\" { \"0\" { \"path\" \""+library.Replace("\\","\\\\")+"\" } }");File.WriteAllText(Path.Combine(library,"steamapps","appmanifest_42.acf"),"\"AppState\" { \"installdir\" \"Test Game\" }");
        Check(SteamGameLifetime.InstallDirectory(42,steam)==common,"Lifetime tracking resolves games in another Steam library with escaped paths");
        File.WriteAllText(Path.Combine(library,"steamapps","appmanifest_43.acf"),"\"AppState\" { \"installdir\" \"..\\\\..\\\\outside\" }");bool escape=false;try{SteamGameLifetime.InstallDirectory(43,steam);}catch(InvalidOperationException){escape=true;}Check(escape,"Lifetime tracking refuses a manifest path escaping the games directory");
        string worker=Path.Combine(dir,"worker");Directory.CreateDirectory(worker);string workerExe=Path.Combine(worker,"RecoveryWorker.exe");File.Copy(Assembly.GetExecutingAssembly().Location,workerExe,true);File.Copy(typeof(LaunchPresetRecovery).Assembly.Location,Path.Combine(worker,Path.GetFileName(typeof(LaunchPresetRecovery).Assembly.Location)),true);
        using(var process=Process.Start(new ProcessStartInfo(workerExe,"--bootstrap"){UseShellExecute=false,CreateNoWindow=true})){
            Check(process.WaitForExit(5000),"Test bootstrapper exits before its worker");
            Check(SteamGameLifetime.RunningDirectories(new[]{worker}).Contains(worker),"Real process tracking retains a child game after its launcher exits");
            Thread.Sleep(2000);Check(!SteamGameLifetime.RunningDirectories(new[]{worker}).Contains(worker),"Real process tracking detects the final worker exit");
        }
        File.WriteAllText(path,"<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///not-allowed'>]><LaunchPresetRecovery version=\"1\">&x;</LaunchPresetRecovery>");bool unsafeXml=false;try{Recovery(path);}catch(System.Xml.XmlException){unsafeXml=true;}Check(unsafeXml,"Recovery rejects external-entity XML");File.Delete(path);
        float horizontal=0.8f,vertical=0.9f;
        Action restoreFov=delegate{r.RestoreReady(Current,delegate(int v){fps=v;},delegate(int v){quality=v;},delegate{return new[]{horizontal,vertical};},delegate(float v){horizontal=v;},delegate(float v){vertical=v;});};
        r=Recovery(path);session=r.Begin(1,a,90,2,144,0,now,1f,0.95f,0.8f,0.9f);fps=144;quality=0;r.Fail(session);r=Recovery(path);restoreFov();
        Check(horizontal==1f&&vertical==0.95f&&fps==90&&quality==2&&!File.Exists(path),"FOV recovery survives reload and restores both original axes");
        session=r.Begin(1,a,90,2,144,0,now,1f,1f,0.8f,0.9f);fps=144;quality=0;horizontal=0.85f;vertical=0.9f;r.Fail(session);restoreFov();
        Check(horizontal==0.85f&&vertical==1f,"Manual horizontal FOV survives while unchanged vertical FOV restores");
        session=r.Begin(1,a,90,2,144,0,now,1f,null,0.8f,null);fps=144;quality=0;horizontal=0.8f;vertical=0.7f;r.Fail(session);restoreFov();
        Check(horizontal==1f&&vertical==0.7f,"An omitted FOV axis is never changed during recovery");
        first=r.Begin(1,a,90,2,144,0,now,1f,null,0.8f,null);second=r.Begin(2,b,144,0,120,2,now,0.8f,null,0.6f,null);fps=120;quality=2;horizontal=0.8f;r.Fail(first);r.Fail(second);restoreFov();
        Check(horizontal==0.8f,"Manual FOV matching an older preset survives overlapping recovery");
        Check(LaunchPresetRecovery.Near(0.80001f,0.8f)&&!LaunchPresetRecovery.Near(0.81f,0.8f)&&!LaunchPresetRecovery.Near(Single.NaN,0.8f),"FOV comparison tolerates roundoff without swallowing a percentage-step change");
        Console.WriteLine("Recovery checks passed: "+checks);return 0;
    }catch(Exception e){Console.WriteLine(e);return 1;}}
}
