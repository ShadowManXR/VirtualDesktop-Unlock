using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using VirtualDesktop.LibraryOverrides;
using VirtualDesktop.Unlock;

public static class BitrateHookTest
{
    private static Assembly assembly;
    private static MethodBase ceiling;
    private static MethodBase calculate;
    private static string path;
    private static int checks;
    private static bool updatedStreamer;
    private const BindingFlags All = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    public static int Main(string[] args)
    {
        try
        {
            path=Environment.GetEnvironmentVariable("VD_BITRATE_CONFIG");
            BitrateConfiguration.Defaults().Save(path);
            assembly=Assembly.LoadFrom(args[0]);
            updatedStreamer=assembly.GetName().Version==new Version(1,34,25,0);
            ceiling=assembly.ManifestModule.ResolveMethod(updatedStreamer?0x060212CD:0x060212B6);
            calculate=assembly.ManifestModule.ResolveMethod(updatedStreamer?0x060212CE:0x060212B7);
            Check(Limit(1000,10,false)==200000000,"Disabled override preserves Quest 3 AV1 ceiling");
            var config=BitrateConfiguration.Defaults();config.Enabled=true;Apply(config);
            Check(Limit(1000,1,false)==800000000 && Limit(1000,2,false)==222000000 && Limit(1000,10,false)==222000000,
                "Quest 3 defaults reach the bitrate hook");
            // Keep the distinct HEVC and AV1 values used to verify encoder routing below.
            config.Profiles[1000]=new BitrateProfile(1000,300,250);Apply(config);
            foreach(int codec in new[]{1,5})Check(Limit(1000,codec,false)==1000000000,"Quest 3 H264/H264+ ceiling codec="+codec);
            foreach(int codec in new[]{2,6})Check(Limit(1000,codec,false)==300000000,"Quest 3 HEVC/HEVC10 ceiling codec="+codec);
            foreach(int codec in new[]{10,11})Check(Limit(1000,codec,false)==250000000,"Quest 3 AV1/AV110 ceiling codec="+codec);
            Check(Limit(1000,11,true)==250000000,"Foveated AV1 uses the headset profile");
            Check(Limit(320,10,false)==150000000 && Limit(1001,10,false)==200000000,"Unconfigured Quest 2 and Quest 3S retain stock ceilings");
            Check(Target(1000,10,1)==250000000 && Target(1000,2,1)==300000000 && Target(1000,1,1)==1000000000,"Original slider calculation reaches all requested ceilings");
            Check(Target(1000,10,0.5f)==130000000,"Original slider continues to scale below the new maximum");
            config.Profiles.Add(1001,new BitrateProfile(800,275,240));Apply(config);
            Check(Limit(1001,10,false)==240000000 && Limit(1000,10,false)==250000000,"Headset-specific AV1 profiles remain independent");
            Check(Limit(1001,6,false)==275000000 && Limit(1001,5,false)==800000000,"Separate codec values apply to Quest 3S only");
            TestEncoderPath();
            TestNativeDiagnostics();
            config.Enabled=false;Apply(config);
            Check(Limit(1000,1,false)==200000000 && Limit(1000,5,false)==500000000 && Limit(1000,2,false)==200000000 && Limit(1000,10,false)==200000000,"Disabling restores stock H264, H264+, HEVC and AV1 ceilings");
            File.WriteAllText(path,"<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///not-allowed'>]><BitrateOverrides version='1' enabled='true'>&x;</BitrateOverrides>");
            BitrateRuntime.RefreshNow(false);
            Check(Limit(1000,10,false)==200000000,"Unsafe XML fails closed to stock bitrate behavior");
            config.Enabled=true;Apply(config);
            Parallel.For(0,400,delegate(int i) { if(Limit(1000,10,(i&1)==0)!=250000000)throw new Exception("Concurrent calculation mismatch"); });
            Check(true,"Concurrent ceiling calls return consistent profile values");
            config.Enabled=false;Apply(config);
            Check(Target(1000,10,1)==200000000,"Restore remains effective after invalid input and re-enable");
            Console.WriteLine("Bitrate server checks passed: "+checks);return 0;
        }
        catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}
    }
    private static object EnumValue(string name,int value){return Enum.ToObject(assembly.GetType(name),value);}
    private static int Limit(int headset,int codec,bool foveated){return (int)ceiling.Invoke(null,new[]{EnumValue("Xenko.VR.HmdType",headset),EnumValue("VirtualDesktop.Net.VideoCodec",codec),(object)foveated});}
    private static int Target(int headset,int codec,float amount){return (int)calculate.Invoke(null,new[]{EnumValue("Xenko.VR.HmdType",headset),(object)amount,EnumValue("VirtualDesktop.Net.VideoCodec",codec),(object)false});}
    private static void Apply(BitrateConfiguration value){value.Save(path);BitrateRuntime.RefreshNow(false);}
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    private static object Singleton(string name)
    {
        var type=assembly.GetType(name);var instance=FormatterServices.GetUninitializedObject(type);
        for(var baseType=type;baseType!=null;baseType=baseType.BaseType)
        {
            var field=baseType.GetField("<Default>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
            if(field!=null){field.SetValue(null,instance);return instance;}
        }
        throw new Exception("Settings singleton not found");
    }
    private static void TestEncoderPath()
    {
        var mobile=Singleton("VirtualDesktop.Mobile.SharedMobileSettings");
        var user=Singleton("VirtualDesktop.Mobile.SharedUserSettings");
        var shared=Singleton("VirtualDesktop.Mobile.SharedStreamerSettings");
        var mobileType=mobile.GetType();var userType=user.GetType();var sharedType=shared.GetType();
        mobileType.GetField("_hmdType",All).SetValue(mobile,EnumValue("Xenko.VR.HmdType",1000));
        mobileType.GetField("_measuredBandwidth",All).SetValue(mobile,1200000000);
        // The original forwarding routine divides the aggregate target between its two views.
        mobileType.GetField("_useSpacewarp",All).SetValue(mobile,true);
        userType.GetField("_vrBitrateLimit",All).SetValue(user,1f);
        var sourceType=assembly.GetType("VirtualDesktop.Net.StreamingSource");
        sharedType.GetField("_streamingSource",All).SetValue(shared,Enum.ToObject(sourceType,1));
        sharedType.GetField("_activeCodec",All).SetValue(shared,EnumValue("VirtualDesktop.Net.VideoCodec",1));
        mobileType.GetMethod("UpdateVRBitrates",All).Invoke(mobile,null);
        Check((int)mobileType.GetProperty("AV1VRBitrate").GetValue(mobile,null)==250000000,"Original UpdateVRBitrates computes 250 Mbps AV1");
        var streamerType=assembly.GetType("VirtualDesktop.Streamer.OculusStreamer");
        var streamer=FormatterServices.GetUninitializedObject(streamerType);
        var slotTokens=updatedStreamer?new[]{0x04000293,0x04000294,0x04000295,0x04000296}:new[]{0x0400028F,0x04000291};
        var slotFields=new List<FieldInfo>();
        foreach(int slotToken in slotTokens)slotFields.Add(assembly.ManifestModule.ResolveField(slotToken));
        int fwdToken=updatedStreamer?0x06000424:0x0600041D;
        // Verified on 1.34.25: wrapper fields are B6=H264, B7=H264+, B8=active-codec
        // target, B9=AV1. The stock VR forward fills the wrappers in slots 93/95;
        // slots 94/96 are left untouched by that routine in this build.
        var rateTokens=updatedStreamer?new[]{0x04014DB6,0x04014DB7,0x04014DB8,0x04014DB9}:new[]{0x04014DAB,0x04014DAC,0x04014DAD};
        var wrapperType=Array.FindLast(assembly.GetTypes(),delegate(Type t){return !t.IsAbstract && slotFields[0].FieldType.IsAssignableFrom(t);});
        var wrappers=new List<object>();
        foreach(var slotField in slotFields)
        {
            var wrapper=FormatterServices.GetUninitializedObject(wrapperType);GC.SuppressFinalize(wrapper);wrappers.Add(wrapper);
        }
        for(int slot=0;slot<slotFields.Count;slot++)slotFields[slot].SetValue(streamer,wrappers[slot]);
        Action forward=delegate(){assembly.ManifestModule.ResolveMethod(fwdToken).Invoke(streamer,null);};
        Func<int,int,int> forwardedRate=delegate(int wrapper,int rate){return (int)assembly.ManifestModule.ResolveField(rateTokens[rate]).GetValue(wrappers[wrapper]);};
        Func<int,int,bool> everyWrapperRate=delegate(int rate,int value){foreach(var wrapper in wrappers){if((int)assembly.ManifestModule.ResolveField(rateTokens[rate]).GetValue(wrapper)!=value)return false;}return true;};
        Func<int,int,bool> pairWrapperRate=delegate(int rate,int value){foreach(int w in new[]{0,2}){if((int)assembly.ManifestModule.ResolveField(rateTokens[rate]).GetValue(wrappers[w])!=value)return false;}return true;};
        forward();
        if(!updatedStreamer)
        {
            Check(forwardedRate(0,2)==250000000 && forwardedRate(1,2)==250000000,"Original Streamer forwarding passes AV1 target to encoder wrappers");
            Check(forwardedRate(0,0)==1000000000 && forwardedRate(0,1)==1000000000,"Original forwarding passes raised H264/H264+ targets");
        }
        else
        {
            Check(pairWrapperRate(0,1000000000) && pairWrapperRate(1,1000000000) && pairWrapperRate(2,1000000000) && pairWrapperRate(3,250000000),"1.34.25 forwarding passes H264/H264+/active/AV1 targets to the VR encoder wrappers");
        }
        sharedType.GetField("_activeCodec",All).SetValue(shared,EnumValue("VirtualDesktop.Net.VideoCodec",6));
        mobileType.GetMethod("UpdateVRBitrates",All).Invoke(mobile,null);
        forward();
        if(!updatedStreamer)Check(forwardedRate(0,0)==300000000,"Switching active codec to HEVC10 updates its encoder target to 300 Mbps");
        else Check(pairWrapperRate(0,1000000000) && pairWrapperRate(1,1000000000) && pairWrapperRate(2,300000000),"1.34.25 forwarding follows the HEVC profile once HEVC10 is active");
        mobileType.GetField("_useSpacewarp",All).SetValue(mobile,false);
        forward();
        if(!updatedStreamer)Check(forwardedRate(0,2)==125000000,"Original per-view allocation is preserved with spacewarp disabled");
        else Check(pairWrapperRate(3,125000000),"1.34.25 forwarding halves the AV1 target once spacewarp is off");
        sharedType.GetField("_activeCodec",All).SetValue(shared,EnumValue("VirtualDesktop.Net.VideoCodec",1));
        mobileType.GetMethod("UpdateVRBitrates",All).Invoke(mobile,null);
        forward();
        if(!updatedStreamer)Check(forwardedRate(0,0)==500000000 && forwardedRate(0,1)==500000000,"1000 Mbps H264 budget is split to 500 Mbps per view without spacewarp");
        else Check(pairWrapperRate(0,500000000) && pairWrapperRate(1,500000000),"1.34.25 H264 budget is split to 500 Mbps per view without spacewarp");
        sharedType.GetField("_activeCodec",All).SetValue(shared,EnumValue("VirtualDesktop.Net.VideoCodec",6));
        mobileType.GetField("_useSpacewarp",All).SetValue(mobile,true);
        // Match the real subscription used to locate OculusStreamer; do not invent a
        // VRBitrateLimitChanged subscription that the installed Streamer never makes.
        var ownerHandler=streamerType.GetMethod("\u001a",All,null,new[]{typeof(object),typeof(EventArgs)},null);
        Console.WriteLine("owner handler token=0x"+ownerHandler.MetadataToken.ToString("X8"));
        mobileType.GetEvent("HmdTypeChanged").AddEventHandler(mobile,Delegate.CreateDelegate(typeof(EventHandler),streamer,ownerHandler));
        var config=BitrateConfiguration.Read(path);config.Enabled=false;config.Save(path);BitrateRuntime.RefreshNow(true);
        Check((int)assembly.ManifestModule.ResolveField(updatedStreamer?0x04014DB9:0x04014DAD).GetValue(wrappers[0])==200000000,"Live disable notification restores existing encoder wrapper target");
        config.Enabled=true;config.Save(path);BitrateRuntime.RefreshNow(true);
        Check((int)assembly.ManifestModule.ResolveField(updatedStreamer?0x04014DB9:0x04014DAD).GetValue(wrappers[0])==250000000,"Live enable notification refreshes existing encoder wrapper target");
        if(!updatedStreamer)Check(BitrateRuntime.CaptureDiagnostics().Contains("encoderTargets=300000000,1000000000,250000000 (NVENC "),"Diagnostics read targets from actual OculusStreamer encoder wrappers");
        else Check(BitrateRuntime.CaptureDiagnostics().Contains("encoderTargets=1000000000,1000000000,300000000,250000000 (NVENC unavailable)|0,0,0,0 (NVENC unavailable)|1000000000,1000000000,300000000,250000000 (NVENC unavailable)|0,0,0,0 (NVENC unavailable)"),"1.34.25 diagnostics read targets from actual OculusStreamer encoder wrappers");
        mobileType.GetField("_measuredBandwidth",All).SetValue(mobile,180000000);
        mobileType.GetMethod("UpdateVRBitrates",All).Invoke(mobile,null);
        Check((int)mobileType.GetProperty("AV1VRBitrate").GetValue(mobile,null)==180000000,"Measured network bandwidth still limits the AV1 target");
        string diagnostics=BitrateRuntime.CaptureDiagnostics();
        Check(diagnostics.Contains("headset=1000;") && diagnostics.Contains("slider=1;") && diagnostics.Contains("measuredBandwidth=180000000;") && diagnostics.Contains(updatedStreamer?"targets=180000000,180000000,180000000,180000000":"targets=180000000,180000000,180000000"),"Live diagnostics report real settings and bandwidth-limited targets");
        Check((int)mobileType.GetProperty("AV1VRBitrate").GetValue(mobile,null)==180000000,"Reading diagnostics leaves stream targets unchanged");
    }
    private static void TestNativeDiagnostics()
    {
        if(updatedStreamer)
        {
            // 1.34.25 keeps bitrate evidence in the managed encoder targets;
            // native NVENC introspection is unavailable for this build.
            // Token 0x04014F7B belongs to 1.34.23 only and must not be resolved here.
            Check(BitrateRuntime.ReadNativeRateControl(new object())=="unavailable","1.34.25 native diagnostics stay unavailable once the encoder targets are verified");
            return;
        }
        var pointer=assembly.ManifestModule.ResolveField(0x04014F7B);
        var wrapper=FormatterServices.GetUninitializedObject(pointer.DeclaringType);GC.SuppressFinalize(wrapper);
        var setter=new DynamicMethod("SetTestConfigurationPointer",typeof(void),new[]{typeof(object),typeof(IntPtr)},typeof(BitrateHookTest),true);
        var il=setter.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,pointer.DeclaringType);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Conv_U);il.Emit(OpCodes.Stfld,pointer);il.Emit(OpCodes.Ret);
        var set=(Action<object,IntPtr>)setter.CreateDelegate(typeof(Action<object,IntPtr>));
        Check(BitrateRuntime.ReadNativeRateControl(wrapper)=="not initialized","Native diagnostics handle an uninitialized encoder");
        var runtime=typeof(BitrateRuntime);
        int average=(int)runtime.GetField("nativeAverageOffset",All).GetValue(null);
        int maximum=(int)runtime.GetField("nativeMaximumOffset",All).GetValue(null);
        var memory=Marshal.AllocHGlobal(Math.Max(average,maximum)+4);
        try
        {
            Marshal.WriteInt32(memory,average,111000000);Marshal.WriteInt32(memory,maximum,100000000);set(wrapper,memory);
            Check(BitrateRuntime.ReadNativeRateControl(wrapper)=="average=111000000,maximum=100000000","Native diagnostics distinguish average and maximum bitrate fields");
            Check(Marshal.ReadInt32(memory,average)==111000000 && Marshal.ReadInt32(memory,maximum)==100000000,"Native diagnostics do not modify rate-control memory");
            set(wrapper,new IntPtr(1));
            Check(BitrateRuntime.ReadNativeRateControl(wrapper)=="unavailable","Native diagnostics safely reject an unreadable configuration pointer");
        }
        finally{set(wrapper,IntPtr.Zero);Marshal.FreeHGlobal(memory);}
    }
}
