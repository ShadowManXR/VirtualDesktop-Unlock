using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using VirtualDesktop.Unlock;

internal static class ButtonRemapHookTest
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static FieldInfo FieldAt(Module module, MethodInfo method, int offset)
    {
        return module.ResolveField(BitConverter.ToInt32(method.GetMethodBody().GetILAsByteArray(), offset + 1));
    }

    private static void Main(string[] args)
    {
        var module = Assembly.LoadFrom(args[0]).ManifestModule;
        var helper = typeof(ButtonRemapProfile).Assembly.GetType("VirtualDesktop.LibraryOverrides.ButtonRemapRuntime", true);
        var writer = (MethodInfo)module.ResolveMethod(0x06000410);
        var target = FormatterServices.GetUninitializedObject(writer.DeclaringType);
        var frameType = writer.GetParameters()[0].ParameterType.GetElementType();
        var trackingField = FieldAt(module, writer, 0x20);
        var inputField = FieldAt(module, writer, 0x25);
        var buttonField = module.ResolveField(0x04014ACE);
        var touchField = module.ResolveField(0x04014ACF);
        var axisField = module.ResolveField(0x04014AD4);

        var setter = new DynamicMethod("SetOwnedMapping", typeof(void), new[] { typeof(object), typeof(IntPtr) }, typeof(ButtonRemapHookTest), true);
        var il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, writer.DeclaringType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Stfld, module.ResolveField(0x0400029A));
        il.Emit(OpCodes.Ret);
        var setPointer = (Action<object, IntPtr>)setter.CreateDelegate(typeof(Action<object, IntPtr>));

        const int buttons = 0x31;
        const int touches = 0x658;
        const short axis = -1001;
        object input = Activator.CreateInstance(inputField.FieldType);
        buttonField.SetValue(input, Enum.ToObject(buttonField.FieldType, buttons));
        touchField.SetValue(input, Enum.ToObject(touchField.FieldType, touches));
        axisField.SetValue(input, axis);
        byte[] pressure = { 31, 192, 64, 128 };
        var pressureFields = new[] { 0x04014AD0, 0x04014AD1, 0x04014AD2, 0x04014AD3 }.Select(module.ResolveField).ToArray();
        for (int i = 0; i < pressure.Length; i++) pressureFields[i].SetValue(input, pressure[i]);
        object tracking = Activator.CreateInstance(trackingField.FieldType);
        inputField.SetValue(tracking, input);
        object frame = Activator.CreateInstance(frameType);
        trackingField.SetValue(frame, tracking);

        IntPtr memory = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.Copy(new byte[4096], 0, memory, 4096);
            setPointer(target, memory);
            var active = helper.GetField("active", PrivateStatic);
            Action<ButtonRemapProfile, int, int, byte[]> check = (profile, expectedButtons, expectedTouches, expectedPressure) =>
            {
                active.SetValue(null, profile);
                writer.Invoke(target, new[] { frame });
                if (Marshal.ReadInt16(memory, 0) != expectedButtons || Marshal.ReadInt16(memory, 2) != expectedTouches ||
                    Marshal.ReadInt16(memory, 8) != axis)
                    throw new Exception("Unexpected published controller state.");
                var source = inputField.GetValue(trackingField.GetValue(frame));
                for (int i = 0; i < pressure.Length; i++)
                    if (Marshal.ReadByte(memory, 4 + i) != expectedPressure[i] || (byte)pressureFields[i].GetValue(source) != pressure[i])
                        throw new Exception("Unexpected analog pressure.");
                if (Convert.ToInt32(buttonField.GetValue(source)) != buttons ||
                    Convert.ToInt32(touchField.GetValue(source)) != touches ||
                    (short)axisField.GetValue(source) != axis)
                    throw new Exception("The source frame was changed.");
            };
            check(null, buttons, touches, pressure);
            helper.GetMethod("InstallWriter", PrivateStatic).Invoke(null, new object[] { module });
            check(null, buttons, touches, pressure);
            check(new ButtonRemapProfile(new[] { new ButtonSwap(RemapButton.A, RemapButton.B),
                new ButtonSwap(RemapButton.X, RemapButton.Y) }), 0x32, 0x654, pressure);
            check(new ButtonRemapProfile(new[] { new ButtonSwap(RemapButton.A, RemapButton.Y) }), 0x38, 0x651, pressure);
            check(new ButtonRemapProfile(new[] { new ButtonSwap(RemapButton.RTrigger, RemapButton.LGrip),
                new ButtonSwap(RemapButton.RThumb, RemapButton.LThumb) }), 0x281, 0x6c8, new byte[] { 31, 64, 192, 128 });
            check(new ButtonRemapProfile(new[] { new ButtonSwap(RemapButton.A, RemapButton.RGrip) }),
                0x70, touches, new byte[] { 31, 192, 64, 255 });
            check(null, buttons, touches, pressure);
            Console.WriteLine("PASS Streamer publisher swaps clicks, touches and analog pressure while preserving stick movement and unrelated inputs");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
