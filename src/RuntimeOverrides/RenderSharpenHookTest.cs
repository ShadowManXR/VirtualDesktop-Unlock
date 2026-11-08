using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;

class RenderSharpenHookTest
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    static int Offset(FieldInfo field){
        var method=new DynamicMethod("offset",typeof(int),Type.EmptyTypes,typeof(RenderSharpenHookTest),true);var il=method.GetILGenerator();var local=il.DeclareLocal(field.DeclaringType);
        il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Ldflda,field);il.Emit(OpCodes.Conv_I);il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Conv_I);il.Emit(OpCodes.Sub);il.Emit(OpCodes.Conv_I4);il.Emit(OpCodes.Ret);
        return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
    }
    static float Float(IntPtr memory,int offset){var bytes=new byte[4];Marshal.Copy(IntPtr.Add(memory,offset),bytes,0,4);return BitConverter.ToSingle(bytes,0);}
    static FieldInfo HandlerField(Assembly assembly,MethodInfo method,int position){var il=method.GetMethodBody().GetILAsByteArray();if(il[position]!=0x7b)throw new Exception("Unexpected handler field IL");return assembly.ManifestModule.ResolveField(BitConverter.ToInt32(il,position+1));}
    static int Main(string[] args){
        IntPtr native=IntPtr.Zero;Timer timer=null;IDisposable data=null;Action<object,IntPtr> pointer=null;object owner=null;
        try{
            using(var sha=SHA256.Create())if(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(args[0]))).Replace("-","")!="0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8")throw new Exception("Unverified Streamer");
            var bridge=Assembly.LoadFrom(args[2]).GetType("VirtualDesktop.LibraryOverrides.LaunchPresetRuntime",true).GetMethod("Set",BindingFlags.Static|BindingFlags.NonPublic);
            var assembly=Assembly.LoadFrom(args[0]);Directory.CreateDirectory(args[1]);
            assembly.ManifestModule.ResolveField(0x04014843).SetValue(null,args[1]);assembly.ManifestModule.ResolveField(0x04014844).SetValue(null,args[1]);
            var type=assembly.GetType("VirtualDesktop.Streamer.StreamerSettings",true);var settings=FormatterServices.GetUninitializedObject(type);
            timer=new Timer(delegate{},null,Timeout.Infinite,Timeout.Infinite);
            for(var t=type;t!=null;t=t.BaseType){
                var field=t.GetField("_saveTimer",Flags|BindingFlags.DeclaredOnly);if(field!=null)field.SetValue(settings,timer);
                field=t.GetField("<Default>k__BackingField",Flags|BindingFlags.DeclaredOnly);if(field!=null)field.SetValue(null,settings);
            }
            type.GetField("_renderResolutionOverride",Flags).SetValue(settings,1f);
            type.GetField("_sharpening",Flags).SetValue(settings,0.7f);
            type.GetField("_useSharpening",Flags).SetValue(settings,false);
            var renderHandler=(MethodInfo)assembly.ManifestModule.ResolveMethod(0x06000433);
            var toggleHandler=(MethodInfo)assembly.ManifestModule.ResolveMethod(0x06000434);
            var amountHandler=(MethodInfo)assembly.ManifestModule.ResolveMethod(0x06000435);
            owner=FormatterServices.GetUninitializedObject(renderHandler.DeclaringType);
            var nativePointer=HandlerField(assembly,renderHandler,8);var bytes=renderHandler.GetMethodBody().GetILAsByteArray();
            if(bytes[0x21]!=0x7d)throw new Exception("Unexpected render forwarding IL");
            var renderField=assembly.ManifestModule.ResolveField(BitConverter.ToInt32(bytes,0x22));int renderOffset=Offset(renderField);
            Check(renderField.FieldType==typeof(float)&&renderOffset>=0&&renderOffset<4092,"Render resolution native float offset="+renderOffset);
            var setter=new DynamicMethod("OwnedPointer",typeof(void),new[]{typeof(object),typeof(IntPtr)},typeof(RenderSharpenHookTest),true);var emit=setter.GetILGenerator();
            emit.Emit(OpCodes.Ldarg_0);emit.Emit(OpCodes.Castclass,nativePointer.DeclaringType);emit.Emit(OpCodes.Ldarg_1);emit.Emit(OpCodes.Conv_U);emit.Emit(OpCodes.Stfld,nativePointer);emit.Emit(OpCodes.Ret);
            pointer=(Action<object,IntPtr>)setter.CreateDelegate(typeof(Action<object,IntPtr>));
            native=Marshal.AllocHGlobal(4096);Marshal.Copy(new byte[4096],0,native,4096);pointer(owner,native);
            var enabledA=HandlerField(assembly,toggleHandler,0x0e);var enabledB=HandlerField(assembly,toggleHandler,0x1f);
            var strengthA=HandlerField(assembly,amountHandler,0x0e);var strengthB=HandlerField(assembly,amountHandler,0x1f);
            var parameter=enabledA.FieldType;var bufferType=assembly.GetType("SharpDX.Toolkit.Graphics.EffectConstantBuffer",true);
            var buffer=FormatterServices.GetUninitializedObject(bufferType);
            data=(IDisposable)Activator.CreateInstance(assembly.GetType("SharpDX.DataBuffer",true),new object[]{64});
            bufferType.GetField("BackingBuffer",Flags).SetValue(buffer,data);
            IntPtr memory=(IntPtr)data.GetType().GetProperty("DataPointer").GetValue(data,null);Marshal.Copy(new byte[64],0,memory,64);
            var fields=new[]{enabledA,enabledB,strengthA,strengthB};
            for(int index=0;index<fields.Length;index++){
                var p=FormatterServices.GetUninitializedObject(parameter);parameter.GetField("buffer",Flags).SetValue(p,buffer);parameter.GetField("offset",Flags).SetValue(p,index*4);fields[index].SetValue(owner,p);
            }
            type.GetEvent("RenderResolutionOverrideChanged").AddEventHandler(settings,Delegate.CreateDelegate(typeof(EventHandler),owner,renderHandler));
            type.GetEvent("UseSharpeningChanged").AddEventHandler(settings,Delegate.CreateDelegate(typeof(EventHandler),owner,toggleHandler));
            type.GetEvent("SharpeningChanged").AddEventHandler(settings,Delegate.CreateDelegate(typeof(EventHandler),owner,amountHandler));
            Action<string,object> set=delegate(string name,object value){bridge.Invoke(null,new object[]{settings,name,value});};
            foreach(float input in new[]{0.5f,0.7f,1f,1.25f,2f,0.3f,2.5f,1.0000001f,1f}){
                set("RenderResolutionOverride",input);float expected=Math.Max(0.5f,Math.Min(2f,input));if(Math.Abs(input-1f)<0.000001f)expected=1f;
                Check((float)type.GetProperty("RenderResolutionOverride").GetValue(settings,null)==expected&&Float(native,renderOffset)==expected,"Render setter/event/native forwarding "+input+" -> "+expected);
            }
            foreach(bool input in new[]{true,false,true}){
                set("UseSharpening",input);
                Check(Marshal.ReadInt32(memory,0)==(input?1:0)&&Marshal.ReadInt32(memory,4)==(input?1:0),"Sharpening switch forwards to both original effect parameters: "+input);
            }
            foreach(float input in new[]{0.05f,0.5f,1f,0.7f}){
                set("Sharpening",input);
                Check(Float(memory,8)==input&&Float(memory,12)==input,"Sharpening strength forwards to both original effect parameters: "+input);
            }
            Check((bool)bufferType.GetField("IsDirty",Flags).GetValue(buffer),"Sharpening changes mark the effect constant buffer dirty for upload");
            set("UseSharpening",false);set("RenderResolutionOverride",1f);set("Sharpening",0.7f);
            Check(Marshal.ReadInt32(memory,0)==0&&Marshal.ReadInt32(memory,4)==0&&Float(native,renderOffset)==1f&&Float(memory,8)==0.7f&&Float(memory,12)==0.7f,"Original render scale, sharpening disabled state and strength can be restored independently");
            Console.WriteLine("Checks passed: "+checks+". Isolated settings, owned VR config memory and CPU-side shader buffers only; no GPU device, live Streamer or game accessed.");return 0;
        }catch(Exception error){Console.WriteLine(error);return 1;}
        finally{if(pointer!=null&&owner!=null)pointer(owner,IntPtr.Zero);if(native!=IntPtr.Zero)Marshal.FreeHGlobal(native);if(data!=null)data.Dispose();if(timer!=null)timer.Dispose();}
    }
}
