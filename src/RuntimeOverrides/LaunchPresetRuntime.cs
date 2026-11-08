using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml;
using System.Linq;
using System.Globalization;
using VirtualDesktop.Unlock;

namespace VirtualDesktop.LibraryOverrides
{
    // Supported binary only. Settings are applied before entering VD's existing
    // async launch state machine; game/runtime selection stays with VD.
    public static class LaunchPresetRuntime
    {
        private static Func<object,Task> original;
        private static FieldInfo platformField,idField,userSingleton,mobileSingleton,streamerSingleton;
        private static MethodInfo connectedGetter;
        private static Dispatcher dispatcher;
        private static Action<string> log;
        private static readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        private static bool installed;
        private static MethodInfo replacementRoot;
        private static LaunchPresetRecovery recovery;
        private static DispatcherTimer recoveryTimer;
        private static int polling;
        private static string lastRecoveryError;
        public static string ConfigurationPath { get { return Environment.GetEnvironmentVariable("VD_LAUNCH_PRESETS_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Virtual Desktop Unlock","LaunchPresets.xml"); } }

        public static void Install(Assembly assembly,Action<string> logger)
        {
            if(installed)return;
            if(IntPtr.Size!=8 || assembly.GetName().Version!=new Version(1,34,25,0))throw new NotSupportedException("Launch presets require x64 Streamer 1.34.25.0.");
            using(var sha=SHA256.Create())if(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(assembly.Location))).Replace("-","")!="0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8")throw new NotSupportedException("Unverified launch-preset binary.");
            // Validate configuration before changing executable memory.
            ReadPreset(1,2981220);
            var entry=(MethodInfo)assembly.ManifestModule.ResolveMethod(0x060001E8);
            var game=assembly.GetType("VirtualDesktop.Interfaces.GameID",true);
            if(!entry.IsStatic || entry.ReturnType!=typeof(Task) || entry.GetParameters().Length!=1 || entry.GetParameters()[0].ParameterType!=game)throw new MissingMethodException("Launch signature changed.");
            platformField=assembly.ManifestModule.ResolveField(0x040000A0);idField=assembly.ManifestModule.ResolveField(0x040000A1);
            if(platformField.DeclaringType!=game || !platformField.FieldType.IsEnum || idField.DeclaringType!=game || idField.FieldType!=typeof(ulong))throw new MissingFieldException("Game identity changed.");
            var state=assembly.ManifestModule.ResolveMethod(0x06000235).DeclaringType;
            var stateField=assembly.ManifestModule.ResolveField(0x04000174);
            var builderField=assembly.ManifestModule.ResolveField(0x04000175);
            var gameField=assembly.ManifestModule.ResolveField(0x04000176);
            original=CreateOriginalLauncher(game,state,stateField,builderField,gameField);
            userSingleton=FindSingleton(assembly.GetType("VirtualDesktop.Mobile.SharedUserSettings",true));
            mobileSingleton=FindSingleton(assembly.GetType("VirtualDesktop.Mobile.SharedMobileSettings",true));
            streamerSingleton=FindSingleton(assembly.GetType("VirtualDesktop.Streamer.StreamerSettings",true));
            connectedGetter=(MethodInfo)assembly.ManifestModule.ResolveMethod(0x06021427);
            if(connectedGetter.IsStatic || connectedGetter.ReturnType!=typeof(bool) || connectedGetter.GetParameters().Length!=0)throw new MissingMethodException("Connection getter changed.");
            if(System.Windows.Application.Current==null)throw new InvalidOperationException("WPF application unavailable.");
            dispatcher=System.Windows.Application.Current.Dispatcher;log=logger;
            recovery=new LaunchPresetRecovery(Path.Combine(Path.GetDirectoryName(ConfigurationPath),"LaunchPresetRecovery.xml"),logger);
            // Emit a matching GameID ABI; boxing is done inside managed IL, never
            // by guessing the layout/register convention of a value-type argument.
            var dynamicAssembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("VDUnlockLaunchAdapter"),AssemblyBuilderAccess.Run);
            var type=dynamicAssembly.DefineDynamicModule("main").DefineType("LaunchAdapter",TypeAttributes.Public|TypeAttributes.Abstract|TypeAttributes.Sealed);
            var method=type.DefineMethod("Launch",MethodAttributes.Public|MethodAttributes.Static,typeof(Task),new[]{game});
            method.SetImplementationFlags(MethodImplAttributes.NoInlining|MethodImplAttributes.NoOptimization);
            var il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Box,game);il.Emit(OpCodes.Call,typeof(LaunchPresetRuntime).GetMethod("Launch"));il.Emit(OpCodes.Ret);
            replacementRoot=type.CreateType().GetMethod("Launch");
            RuntimeHelpers.PrepareMethod(entry.MethodHandle);RuntimeHelpers.PrepareMethod(replacementRoot.MethodHandle);
            IntPtr address=entry.MethodHandle.GetFunctionPointer();
            byte[] expected={0x55,0x41,0x56,0x57,0x56,0x53,0x48,0x81,0xec,0x90,0x00,0x00,0x00};
            var actual=new byte[expected.Length];Marshal.Copy(address,actual,0,actual.Length);
            for(int i=0;i<expected.Length;i++)if(actual[i]!=expected[i])throw new NotSupportedException("Unexpected launch JIT entry; no hook applied.");
            var jump=new byte[12];jump[0]=0x48;jump[1]=0xb8;Array.Copy(BitConverter.GetBytes(replacementRoot.MethodHandle.GetFunctionPointer().ToInt64()),0,jump,2,8);jump[10]=0xff;jump[11]=0xe0;
            uint old;if(!VirtualProtect(address,(UIntPtr)jump.Length,0x40,out old))throw new System.ComponentModel.Win32Exception();
            try{Marshal.Copy(jump,0,address,jump.Length);FlushInstructionCache(Process.GetCurrentProcess().Handle,address,(UIntPtr)jump.Length);}
            finally{uint ignored;VirtualProtect(address,(UIntPtr)jump.Length,old,out ignored);}
            installed=true;log("Launch preset hook installed; Steam game profiles configuration="+ConfigurationPath);
            recoveryTimer=new DispatcherTimer(DispatcherPriority.Background);recoveryTimer.Interval=TimeSpan.FromSeconds(2);
            recoveryTimer.Tick+=async delegate {await PollRecovery();};recoveryTimer.Start();
            log("Launch preset exit recovery active; pending sessions="+recovery.Sessions.Count);
        }

        // Recreate only the compiler's async entry wrapper. The original MoveNext,
        // its exceptions, awaits, game lookup and launcher remain untouched.
        public static Func<object,Task> CreateOriginalLauncher(Type game,Type state,FieldInfo stateField,FieldInfo builderField,FieldInfo gameField)
        {
            if(!state.IsValueType || !typeof(IAsyncStateMachine).IsAssignableFrom(state) || stateField.DeclaringType!=state || stateField.FieldType!=typeof(int) || builderField.DeclaringType!=state || builderField.FieldType!=typeof(AsyncTaskMethodBuilder) || gameField.DeclaringType!=state || gameField.FieldType!=game)throw new NotSupportedException("Async launch state machine changed.");
            var method=new DynamicMethod("VDUnlockOriginalLaunch",typeof(Task),new[]{typeof(object)},typeof(LaunchPresetRuntime),true);
            var il=method.GetILGenerator();var local=il.DeclareLocal(state);
            il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Initobj,state);
            il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Call,typeof(AsyncTaskMethodBuilder).GetMethod("Create"));il.Emit(OpCodes.Stfld,builderField);
            il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Unbox_Any,game);il.Emit(OpCodes.Stfld,gameField);
            il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Ldc_I4_M1);il.Emit(OpCodes.Stfld,stateField);
            il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Ldflda,builderField);il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Call,typeof(AsyncTaskMethodBuilder).GetMethod("Start").MakeGenericMethod(state));
            il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Ldflda,builderField);il.Emit(OpCodes.Call,typeof(AsyncTaskMethodBuilder).GetProperty("Task").GetGetMethod());il.Emit(OpCodes.Ret);
            return (Func<object,Task>)method.CreateDelegate(typeof(Func<object,Task>));
        }

        private static LaunchPreset ReadProfile(int platform,ulong appId)
        {
            if(platform!=1 || appId>UInt32.MaxValue)return null;
            var config=LaunchPresetConfiguration.Read(ConfigurationPath);LaunchPreset preset;
            return config.Games.TryGetValue((uint)appId,out preset)?preset:null;
        }
        public static int[] ReadPreset(int platform,ulong appId)
        {
            var preset=ReadProfile(platform,appId);return preset==null?null:new[]{preset.Fps,preset.Quality};
        }

        public static Task Launch(object game)
        {
            int platform=Convert.ToInt32(platformField.GetValue(game));ulong id=Convert.ToUInt64(idField.GetValue(game));
            if(platform!=1)return original(game);
            LaunchPreset preset;
            try{preset=ReadProfile(platform,id);}
            catch(Exception e){log("Launch preset configuration rejected: "+e.Message);return Failed(e);}
            if(preset==null)return original(game);
            return LaunchWithPreset(game,preset);
        }
        private static async Task LaunchWithPreset(object game,LaunchPreset preset)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            PresetRecoverySession session=null;
            try{
              Exception failure=null;
              try{
                uint id=Convert.ToUInt32(idField.GetValue(game));
                await OnDispatcher(delegate{RestoreReady();User();}).ConfigureAwait(false);
                // Repeated requests for an already running game must not replace
                // the baseline with the preset itself.
                if(recovery.Active(id)!=null){await original(game).ConfigureAwait(false);return;}
                string directory=await Task.Run(delegate{return SteamGameLifetime.InstallDirectory(id);}).ConfigureAwait(false);
                if((await Task.Run(delegate{return SteamGameLifetime.RunningDirectories(new[]{directory});}).ConfigureAwait(false)).Contains(directory)){
                    log("Launch preset retained current settings for already running Steam "+id);await original(game).ConfigureAwait(false);return;
                }
                await OnDispatcher(delegate{
                    var user=User();session=recovery.Begin(id,directory,Convert.ToInt32(Property(user,"VRFramerate")),Convert.ToInt32(Property(user,"VRGraphicsQuality")),preset.Fps,preset.Quality,DateTime.UtcNow,preset.HorizontalFov.HasValue?(float?)Convert.ToSingle(Property(StreamerSettings(),"HorizontalFovTangent")):null,preset.VerticalFov.HasValue?(float?)Convert.ToSingle(Property(StreamerSettings(),"VerticalFovTangent")):null,preset.HorizontalFov,preset.VerticalFov);
                    ApplyPreset(preset);
                }).ConfigureAwait(false);
                // VD exposes no client acknowledgement here. Allow transport/UI to
                // settle, then verify the selection has not changed before launch.
                await Task.Delay(1000).ConfigureAwait(false);
                await OnDispatcher(delegate{CheckPreset(preset);}).ConfigureAwait(false);
                log("Launch preset releasing Steam "+idField.GetValue(game)+"; fps="+preset.Fps+"; quality="+preset.Quality+"; horizontalFov="+FovText(preset.HorizontalFov)+"; verticalFov="+FovText(preset.VerticalFov)+"; utc="+DateTime.UtcNow.ToString("O"));
                await original(game).ConfigureAwait(false);
                try{await ObserveRecovery().ConfigureAwait(false);}catch(Exception observation){RecoveryError(observation);}
              }catch(Exception e){failure=e;}
              if(failure!=null){
                if(session!=null){
                    try{
                        var running=await Task.Run(delegate{return SteamGameLifetime.RunningDirectories(new[]{session.Directory});}).ConfigureAwait(false);
                        await OnDispatcher(delegate{if(!running.Contains(session.Directory))recovery.Fail(session);RestoreReady();}).ConfigureAwait(false);
                    }catch(Exception restore){RecoveryError(restore);}
                }
                log("Launch preset cancelled launch: "+failure.GetBaseException().Message);ExceptionDispatchInfo.Capture(failure).Throw();
              }
            }
            finally{gate.Release();}
        }
        private static async Task ObserveRecovery()
        {
            var directories=recovery.Sessions.Where(s=>!s.Ended).Select(s=>s.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var running=await Task.Run(delegate{return SteamGameLifetime.RunningDirectories(directories);}).ConfigureAwait(false);
            await OnDispatcher(delegate{recovery.Observe(DateTime.UtcNow,running.Contains);RestoreReady();}).ConfigureAwait(false);
        }
        private static void RestoreReady()
        {
            recovery.RestoreReady(delegate{var user=User();return new[]{Convert.ToInt32(Property(user,"VRFramerate")),Convert.ToInt32(Property(user,"VRGraphicsQuality"))};},delegate(int value){Set(User(),"VRFramerate",value);},delegate(int value){Set(User(),"VRGraphicsQuality",value);},delegate{var settings=StreamerSettings();return new[]{Convert.ToSingle(Property(settings,"HorizontalFovTangent")),Convert.ToSingle(Property(settings,"VerticalFovTangent"))};},delegate(float value){Set(StreamerSettings(),"HorizontalFovTangent",value);},delegate(float value){Set(StreamerSettings(),"VerticalFovTangent",value);});
        }
        private static void RecoveryError(Exception error)
        {
            string message=error.GetBaseException().Message;if(lastRecoveryError!=message){lastRecoveryError=message;log("Launch preset recovery pending: "+message);}
        }
        private static async Task PollRecovery()
        {
            if(Interlocked.CompareExchange(ref polling,1,0)!=0)return;
            await gate.WaitAsync().ConfigureAwait(false);
            try{if(recovery.Sessions.Count>0)await ObserveRecovery().ConfigureAwait(false);lastRecoveryError=null;}
            catch(Exception error){RecoveryError(error);}
            finally{gate.Release();Interlocked.Exchange(ref polling,0);}
        }
        private static async Task OnDispatcher(Action action)
        {
            var op=dispatcher.InvokeAsync(action,DispatcherPriority.Send);
            if(await Task.WhenAny(op.Task,Task.Delay(8000)).ConfigureAwait(false)!=op.Task){op.Abort();throw new TimeoutException("Timed out preparing VD launch preset.");}
            await op.Task.ConfigureAwait(false);
        }
        private static object User()
        {
            var user=userSingleton.GetValue(null);var mobile=mobileSingleton.GetValue(null);
            if(user==null || mobile==null || !Object.Equals(Property(user,"IsInitialized"),true) || Convert.ToInt32(Property(mobile,"HmdType"))!=1000)throw new InvalidOperationException("Launch preset requires initialized Quest 3 settings.");
            object client=null;for(var t=user.GetType();t!=null;t=t.BaseType){var f=t.GetField("_client",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(f!=null){client=f.GetValue(user);break;}}
            if(client==null || !(bool)connectedGetter.Invoke(client,null))throw new InvalidOperationException("Headset disconnected; launch preset not applied.");
            return user;
        }
        private static void ApplyPreset(LaunchPreset preset)
        {
            var user=User();Set(user,"VRGraphicsQuality",preset.Quality);Set(user,"VRFramerate",preset.Fps);
            if(preset.HorizontalFov.HasValue)Set(StreamerSettings(),"HorizontalFovTangent",preset.HorizontalFov.Value);
            if(preset.VerticalFov.HasValue)Set(StreamerSettings(),"VerticalFovTangent",preset.VerticalFov.Value);
            log("Launch preset applied before game launch; fps="+preset.Fps+"; quality="+preset.Quality+"; horizontalFov="+FovText(preset.HorizontalFov)+"; verticalFov="+FovText(preset.VerticalFov)+"; utc="+DateTime.UtcNow.ToString("O"));
        }
        private static void CheckPreset(LaunchPreset preset)
        {
            var user=User();if(Convert.ToInt32(Property(user,"VRFramerate"))!=preset.Fps || Convert.ToInt32(Property(user,"VRGraphicsQuality"))!=preset.Quality)throw new InvalidOperationException("VD preset changed during launch preparation; try again.");
            if((preset.HorizontalFov.HasValue&&!LaunchPresetRecovery.Near(Convert.ToSingle(Property(StreamerSettings(),"HorizontalFovTangent")),preset.HorizontalFov.Value))||(preset.VerticalFov.HasValue&&!LaunchPresetRecovery.Near(Convert.ToSingle(Property(StreamerSettings(),"VerticalFovTangent")),preset.VerticalFov.Value)))throw new InvalidOperationException("FOV tangent changed during launch preparation; try again.");
        }
        private static object StreamerSettings(){var settings=streamerSingleton.GetValue(null);if(settings==null)throw new InvalidOperationException("Streamer FOV settings unavailable.");return settings;}
        private static string FovText(float? value){return value.HasValue?value.Value.ToString("R",CultureInfo.InvariantCulture):"unchanged";}
        private static object Property(object value,string name){return value.GetType().GetProperty(name).GetValue(value,null);}
        private static void Set(object value,string name,object setting){var p=value.GetType().GetProperty(name);p.SetValue(value,p.PropertyType.IsEnum?Enum.ToObject(p.PropertyType,setting):Convert.ChangeType(setting,p.PropertyType,CultureInfo.InvariantCulture),null);}
        private static FieldInfo FindSingleton(Type t){for(;t!=null;t=t.BaseType){var f=t.GetField("<Default>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(f!=null)return f;}throw new MissingFieldException("Settings singleton");}
        private static Task Failed(Exception e){var completion=new TaskCompletionSource<bool>();completion.SetException(e);return completion.Task;}
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool VirtualProtect(IntPtr address,UIntPtr size,uint protection,out uint old);
        [DllImport("kernel32.dll")]private static extern bool FlushInstructionCache(IntPtr process,IntPtr address,UIntPtr size);
    }
}
