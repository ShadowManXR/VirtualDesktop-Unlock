using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

class FovHookTest
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static int Offset(FieldInfo field){
        var method=new DynamicMethod("offset",typeof(int),Type.EmptyTypes,typeof(FovHookTest),true);var il=method.GetILGenerator();var local=il.DeclareLocal(field.DeclaringType);
        il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Ldflda,field);il.Emit(OpCodes.Conv_I);il.Emit(OpCodes.Ldloca,local);il.Emit(OpCodes.Conv_I);il.Emit(OpCodes.Sub);il.Emit(OpCodes.Conv_I4);il.Emit(OpCodes.Ret);
        return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
    }
    static void Write(IntPtr memory,int offset,float value){var bytes=BitConverter.GetBytes(value);Marshal.Copy(bytes,0,IntPtr.Add(memory,offset),4);}
    static float Read(IntPtr memory,int offset){var bytes=new byte[4];Marshal.Copy(IntPtr.Add(memory,offset),bytes,0,4);return BitConverter.ToSingle(bytes,0);}
    static int Main(string[] args){
        IntPtr block=IntPtr.Zero;Timer timer=null;Action<object,IntPtr> pointer=null;object owner=null;
        try{
            using(var sha=SHA256.Create())if(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(args[0]))).Replace("-","")!="0229A322137F55687240D2B51F5F7002056D9277D7FA001EC6CB4EB25AF0BFB8")throw new Exception("Unverified Streamer binary");
            var helper=Assembly.LoadFrom(args[2]);
            var bridge=helper.GetType("VirtualDesktop.LibraryOverrides.LaunchPresetRuntime",true).GetMethod("Set",BindingFlags.Static|BindingFlags.NonPublic);
            var a=Assembly.LoadFrom(args[0]);Directory.CreateDirectory(args[1]);
            a.ManifestModule.ResolveField(0x04014843).SetValue(null,args[1]);a.ManifestModule.ResolveField(0x04014844).SetValue(null,args[1]);
            var type=a.GetType("VirtualDesktop.Streamer.StreamerSettings",true);var settings=FormatterServices.GetUninitializedObject(type);
            // Storage was redirected before singleton type initialization. Provide
            // a no-op timer to the synthetic instance so the tested setters do
            // not persist their changes or invoke normal application startup.
            timer=new Timer(delegate{},null,Timeout.Infinite,Timeout.Infinite);
            for(var t=type;t!=null;t=t.BaseType){
                var f=t.GetField("_saveTimer",Flags|BindingFlags.DeclaredOnly);if(f!=null)f.SetValue(settings,timer);
                f=t.GetField("<Default>k__BackingField",Flags|BindingFlags.DeclaredOnly);if(f!=null)f.SetValue(null,settings);
            }
            type.GetField("_horizontalFovTangent",Flags).SetValue(settings,1f);type.GetField("_verticalFovTangent",Flags).SetValue(settings,1f);type.GetField("_useFovStencil",Flags).SetValue(settings,true);
            var horizontal=(MethodInfo)a.ManifestModule.ResolveMethod(0x06000431);var vertical=(MethodInfo)a.ManifestModule.ResolveMethod(0x06000432);
            var hb=horizontal.GetMethodBody().GetILAsByteArray();var vb=vertical.GetMethodBody().GetILAsByteArray();
            if(hb[8]!=0x7b||hb[0x21]!=0x7d||vb[8]!=0x7b||vb[0x21]!=0x7d)throw new Exception("Native forwarding IL changed");
            var nativePointer=a.ManifestModule.ResolveField(BitConverter.ToInt32(hb,9));var hField=a.ManifestModule.ResolveField(BitConverter.ToInt32(hb,0x22));var vField=a.ManifestModule.ResolveField(BitConverter.ToInt32(vb,0x22));
            Check(nativePointer.MetadataToken==BitConverter.ToInt32(vb,9)&&nativePointer.FieldType.IsPointer&&nativePointer.FieldType.GetElementType()==hField.DeclaringType&&hField.DeclaringType==vField.DeclaringType&&hField.FieldType==typeof(float)&&vField.FieldType==typeof(float),"Original H/V handlers use the same native VR configuration and distinct float fields");
            int hOffset=Offset(hField),vOffset=Offset(vField);Check(hOffset>=0&&vOffset>=0&&hOffset<4092&&vOffset<4092&&hOffset!=vOffset,"Verified native field offsets: horizontal="+hOffset+", vertical="+vOffset);
            owner=FormatterServices.GetUninitializedObject(horizontal.DeclaringType);
            var setter=new DynamicMethod("SetIsolatedNativePointer",typeof(void),new[]{typeof(object),typeof(IntPtr)},typeof(FovHookTest),true);var emit=setter.GetILGenerator();emit.Emit(OpCodes.Ldarg_0);emit.Emit(OpCodes.Castclass,nativePointer.DeclaringType);emit.Emit(OpCodes.Ldarg_1);emit.Emit(OpCodes.Conv_U);emit.Emit(OpCodes.Stfld,nativePointer);emit.Emit(OpCodes.Ret);pointer=(Action<object,IntPtr>)setter.CreateDelegate(typeof(Action<object,IntPtr>));
            block=Marshal.AllocHGlobal(4096);Marshal.Copy(new byte[4096],0,block,4096);Write(block,hOffset,1f);Write(block,vOffset,1f);pointer(owner,block);
            type.GetEvent("HorizontalFovTangentChanged").AddEventHandler(settings,Delegate.CreateDelegate(typeof(EventHandler),owner,horizontal));
            type.GetEvent("VerticalFovTangentChanged").AddEventHandler(settings,Delegate.CreateDelegate(typeof(EventHandler),owner,vertical));
            foreach(string axis in new[]{"HorizontalFovTangent","VerticalFovTangent"}){
                var property=type.GetProperty(axis);Console.WriteLine("Property metadata: "+axis+" "+String.Join("; ",Array.ConvertAll(CustomAttributeData.GetCustomAttributes(property).ToArray(),x=>x.ToString())));
                foreach(float input in new[]{0.9f,0.8f,0.4f,0.2f,1.2f,1f}){
                    int selectedOffset=axis[0]=='H'?hOffset:vOffset,otherOffset=axis[0]=='H'?vOffset:hOffset;float other=Read(block,otherOffset),expected=Math.Max(0.4f,Math.Min(1f,input));
                    bridge.Invoke(null,new object[]{settings,axis,input});float actual=(float)property.GetValue(settings,null);
                    Check(actual==expected&&Read(block,selectedOffset)==expected&&Read(block,otherOffset)==other,"Helper float bridge/original setter/event/native forwarding "+axis+" "+input+" -> "+actual+"; other axis unchanged");
                }
            }
            Check((bool)type.GetProperty("UseFovStencil").GetValue(settings,null),"FOV tangent changes leave FOV stencil unchanged");
            Console.WriteLine("FOV checks passed: "+checks+". Isolated objects and owned memory only; no encoder, game or running Streamer accessed.");return 0;
        }catch(Exception e){Console.WriteLine(e);return 1;}
        finally{if(pointer!=null&&owner!=null)pointer(owner,IntPtr.Zero);if(block!=IntPtr.Zero)Marshal.FreeHGlobal(block);if(timer!=null)timer.Dispose();}
    }
}
