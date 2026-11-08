using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using VirtualDesktop.LibraryOverrides;

struct TestGame {public int Id;}
class LaunchPresetTests
{
    static int checks,captured;
    static void Check(bool condition,string text){if(!condition)throw new Exception(text);Console.WriteLine("PASS "+text);checks++;}
    static async Task Original(TestGame game){await Task.Delay(10);captured=game.Id;if(game.Id==2)throw new InvalidOperationException("expected");}
    static FieldInfo Field(Type state,Type fieldType){foreach(var f in state.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))if(f.FieldType==fieldType)return f;throw new MissingFieldException();}
    static int Main(string[] args){try{
        string dir=args[0];Directory.CreateDirectory(dir);string path=Path.Combine(dir,"LaunchPresets.xml");Environment.SetEnvironmentVariable("VD_LAUNCH_PRESETS_FILE",path);
        Check(LaunchPresetRuntime.ReadPreset(1,2981220)==null,"Missing configuration disables preset");
        File.WriteAllText(path,"<LaunchPresets version=\"1\"><Game platform=\"Steam\" appId=\"2981220\" fps=\"144\" quality=\"0\"/></LaunchPresets>");
        var preset=LaunchPresetRuntime.ReadPreset(1,2981220);Check(preset[0]==144&&preset[1]==0,"Forefront resolves to 144 FPS / Low");
        Check(LaunchPresetRuntime.ReadPreset(1,952060)==null&&LaunchPresetRuntime.ReadPreset(0,2981220)==null,"Other game and platform have no preset");
        foreach(string content in new[]{"<LaunchPresets version=\"3\"/>","<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///not-allowed'>]><LaunchPresets version=\"1\">&x;</LaunchPresets>","<LaunchPresets version=\"1\"><Game platform=\"Steam\" appId=\"2981220\" fps=\"200\" quality=\"0\"/></LaunchPresets>","<LaunchPresets version=\"1\"><Game platform=\"Steam\" appId=\"2981220\" fps=\"144\" quality=\"99\"/></LaunchPresets>","<LaunchPresets version=\"1\"><Game platform=\"Steam\" appId=\"2981220\" fps=\"144\" quality=\"0\"/><Game platform=\"Steam\" appId=\"2981220\" fps=\"90\" quality=\"1\"/></LaunchPresets>"}){
            File.WriteAllText(path,content);bool rejected=false;try{LaunchPresetRuntime.ReadPreset(1,2981220);}catch{rejected=true;}Check(rejected,"Rejects unsafe or invalid configuration");
        }
        var attr=(AsyncStateMachineAttribute)Attribute.GetCustomAttribute(typeof(LaunchPresetTests).GetMethod("Original",BindingFlags.NonPublic|BindingFlags.Static),typeof(AsyncStateMachineAttribute));
        var state=attr.StateMachineType;
        var launch=LaunchPresetRuntime.CreateOriginalLauncher(typeof(TestGame),state,Field(state,typeof(int)),Field(state,typeof(AsyncTaskMethodBuilder)),Field(state,typeof(TestGame)));
        launch(new TestGame{Id=1}).GetAwaiter().GetResult();Check(captured==1,"Preserved async launcher resumes with original game identity");
        bool faulted=false;try{launch(new TestGame{Id=2}).GetAwaiter().GetResult();}catch(InvalidOperationException e){faulted=e.Message=="expected";}Check(faulted,"Preserved async launcher propagates original failure");
        Task.WaitAll(launch(new TestGame{Id=3}),launch(new TestGame{Id=4}));Check(captured==3||captured==4,"Independent preserved state machines can complete");
        Console.WriteLine("Launch preset checks passed: "+checks);return 0;
    }catch(Exception e){Console.WriteLine(e);return 1;}}
}
