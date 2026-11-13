using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using VirtualDesktop.Unlock;

namespace VirtualDesktop.LibraryOverrides
{
    internal static class ButtonRemapRuntime
    {
        private const int WriterToken = 0x06000410;
        private const int ControllerStoreOffset = 0x002A;
        private static readonly object gate = new object();
        private static DynamicMethod writer;
        private static Timer timer;
        private static Action<string> log;
        private static Func<bool> supportsInput;
        private static Session session;
        private static ButtonRemapProfile active;
        private static int polling;

        internal sealed class Session
        {
            internal readonly ButtonRemapProfile Profile;
            internal readonly string Directory;
            internal readonly Stopwatch Age = Stopwatch.StartNew();
            internal TimeSpan? LastSeen;

            internal Session(ButtonRemapProfile profile, string directory)
            {
                Profile = profile;
                Directory = directory;
            }
        }

        // Called only after Manager and the launch hook validate the exact Streamer binary.
        internal static void Install(Assembly assembly, Func<bool> supported, Action<string> logger)
        {
            InstallWriter(assembly.ManifestModule);
            log = logger;
            supportsInput = supported;
            timer = new Timer(Poll, null, 0, 500);
            log("Button remap hook installed at the Streamer controller-state publisher.");
        }

        internal static Session BeginLaunch(int platform, ulong id)
        {
            if (timer == null) return null;
            lock (gate)
            {
                Volatile.Write(ref active, null);
                session = null;
                if (platform != 1 || id == 0 || id > UInt32.MaxValue) return null;
                try
                {
                    var profile = ButtonRemapProfile.Read(ButtonRemapProfile.DefaultDirectory, (uint)id);
                    if (profile.Swaps.Count != 0)
                        session = new Session(profile, SteamGameLifetime.InstallDirectory((uint)id));
                }
                catch (Exception error)
                {
                    log("Button remap unavailable for Steam " + id + ": " + error.GetBaseException().Message);
                }
                return session;
            }
        }

        internal static async Task ObserveLaunch(Session started, Task launch)
        {
            try { await launch.ConfigureAwait(false); }
            catch
            {
                Cancel(started);
                throw;
            }
        }

        internal static void Cancel(Session started)
        {
            lock (gate)
            {
                if (session != started) return;
                session = null;
                Volatile.Write(ref active, null);
            }
        }

        private static void Poll(object unused)
        {
            if (Interlocked.Exchange(ref polling, 1) != 0) return;
            try
            {
                Session current;
                lock (gate) current = session;
                if (current == null) return;

                bool running = SteamGameLifetime.RunningDirectories(new[] { current.Directory }).Contains(current.Directory);
                bool apply = running && supportsInput();
                lock (gate)
                {
                    if (session != current) return;
                    var age = current.Age.Elapsed;
                    if (running) current.LastSeen = age;
                    else if (current.LastSeen.HasValue ? age - current.LastSeen.Value >= TimeSpan.FromSeconds(2) :
                        age >= TimeSpan.FromSeconds(45))
                        session = null;
                    var profile = apply ? current.Profile : null;
                    if (Interlocked.Exchange(ref active, profile) != profile)
                        log(apply ? "Button remaps active for " + current.Directory : "Button remaps cleared.");
                }
            }
            catch (Exception error)
            {
                lock (gate) Volatile.Write(ref active, null);
                log("Button remap polling failed: " + error.GetBaseException().Message);
            }
            finally { Volatile.Write(ref polling, 0); }
        }

        private static DynamicMethod CreatePublisher(Module module, FieldInfo output)
        {
            Type state = output.FieldType;
            var flags = new[] { module.ResolveField(0x04014ACE), module.ResolveField(0x04014ACF) };
            foreach (var field in flags)
            {
                if (field.DeclaringType != state || !field.FieldType.IsEnum ||
                    Enum.GetUnderlyingType(field.FieldType) != typeof(short))
                    throw new NotSupportedException("Controller button layout changed.");
            }
            foreach (RemapButton button in Enum.GetValues(typeof(RemapButton)))
            {
                if (Convert.ToInt32(Enum.Parse(flags[0].FieldType, button.ToString())) != (int)button ||
                    (ButtonRemapProfile.TouchMask(button) != 0 &&
                    Convert.ToInt32(Enum.Parse(flags[1].FieldType, button.ToString())) != (int)button))
                    throw new NotSupportedException("Controller input flags changed.");
            }
            // These four bytes are decoded into LibOVR's left/right IndexTrigger
            // and HandTrigger arrays, in that order.
            var pressure = new[] { 0x04014AD0, 0x04014AD1, 0x04014AD2, 0x04014AD3 }.Select(module.ResolveField).ToArray();
            if (pressure.Any(field => field.DeclaringType != state || field.FieldType != typeof(byte)))
                throw new NotSupportedException("Controller pressure layout changed.");

            var publisher = new DynamicMethod("VDUnlockPublishController", typeof(void),
                new[] { output.DeclaringType.MakePointerType(), state }, typeof(ButtonRemapRuntime), true);
            ILGenerator il = publisher.GetILGenerator();
            LocalBuilder profile = il.DeclareLocal(typeof(ButtonRemapProfile));
            Label publish = il.DefineLabel();
            il.Emit(OpCodes.Call, typeof(ButtonRemapRuntime).GetMethod("ActiveProfile", BindingFlags.NonPublic | BindingFlags.Static));
            il.Emit(OpCodes.Stloc, profile);
            il.Emit(OpCodes.Ldloc, profile);
            il.Emit(OpCodes.Brfalse_S, publish);
            il.Emit(OpCodes.Ldloc, profile);
            foreach (var field in flags.Concat(pressure))
            {
                il.Emit(OpCodes.Ldarga_S, (byte)1);
                il.Emit(OpCodes.Ldflda, field);
            }
            il.Emit(OpCodes.Call, typeof(ButtonRemapProfile).GetMethod("Apply"));
            il.MarkLabel(publish);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, output);
            il.Emit(OpCodes.Ret);
            return publisher;
        }

        private static ButtonRemapProfile ActiveProfile() { return Volatile.Read(ref active); }
        private static void InstallWriter(Module module)
        {
            var original = (MethodInfo)module.ResolveMethod(WriterToken);
            Type frame = original.GetParameters().Single().ParameterType;
            if (original.IsStatic || original.ReturnType != typeof(void) || !frame.IsByRef)
                throw new NotSupportedException("Controller writer signature changed.");

            var body = original.GetMethodBody();
            byte[] code = body.GetILAsByteArray();
            if (code[ControllerStoreOffset] != 0x7D || body.LocalVariables.Count != 0)
                throw new NotSupportedException("Controller writer layout changed.");
            FieldInfo output = module.ResolveField(BitConverter.ToInt32(code, ControllerStoreOffset + 1));
            if (output.MetadataToken != 0x04000366)
                throw new NotSupportedException("Controller state field changed.");

            // Replace the single stfld instruction in a managed copy of the original
            // writer. The branch destinations, publication order and exception handler
            // stay intact. The filter edits only the by-value copy being published.
            DynamicMethod publish = CreatePublisher(module, output);
            writer = new DynamicMethod("VDUnlockControllerWriter", typeof(void),
                new[] { original.DeclaringType, frame }, typeof(ButtonRemapRuntime), true);
            DynamicILInfo il = writer.GetDynamicILInfo();
            RewriteTokens(module, il, code);
            code[ControllerStoreOffset] = 0x28;
            WriteInt(code, ControllerStoreOffset + 1, il.GetTokenFor(publish));
            il.SetCode(code, body.MaxStackSize);
            il.SetLocalSignature(new byte[] { 0x07, 0x00 });
            var handler = body.ExceptionHandlingClauses.Single();
            if (handler.Flags != ExceptionHandlingClauseOptions.Clause)
                throw new NotSupportedException("Controller writer exception handler changed.");
            var eh = new byte[28];
            eh[0] = 0x41; // Fat exception-handling section.
            eh[1] = (byte)eh.Length;
            WriteInt(eh, 8, handler.TryOffset);
            WriteInt(eh, 12, handler.TryLength);
            WriteInt(eh, 16, handler.HandlerOffset);
            WriteInt(eh, 20, handler.HandlerLength);
            WriteInt(eh, 24, il.GetTokenFor(handler.CatchType.TypeHandle));
            il.SetExceptions(eh);
            var descriptor = typeof(DynamicMethod).GetMethod("GetMethodDescriptor", BindingFlags.Instance | BindingFlags.NonPublic);
            var handle = (RuntimeMethodHandle)descriptor.Invoke(writer, null);
            byte[] expected = { 0x55, 0x57, 0x56, 0x48, 0x83, 0xEC, 0x40, 0xC5, 0xF8, 0x77, 0x48, 0x8D, 0x6C, 0x24, 0x50 };
            LaunchPresetRuntime.Redirect(original, handle, expected);
        }

        private static void RewriteTokens(Module module, DynamicILInfo il, byte[] code)
        {
            var opcodes = new Dictionary<short, OperandType>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var opcode = (OpCode)field.GetValue(null);
                opcodes[opcode.Value] = opcode.OperandType;
            }

            for (int index = 0; index < code.Length;)
            {
                short opcode = code[index++];
                if (opcode == 0xFE) opcode = unchecked((short)(0xFE00 | code[index++]));
                switch (opcodes[opcode])
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: index++; break;
                    case OperandType.InlineVar: index += 2; break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR: index += 4; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: index += 8; break;
                    case OperandType.InlineSwitch: index += 4 + 4 * BitConverter.ToInt32(code, index); break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        var member = module.ResolveMember(BitConverter.ToInt32(code, index));
                        var field = member as FieldInfo;
                        var method = member as MethodBase;
                        int token = field != null ? il.GetTokenFor(field.FieldHandle, field.DeclaringType.TypeHandle)
                            : method != null ? il.GetTokenFor(method.MethodHandle, method.DeclaringType.TypeHandle)
                            : il.GetTokenFor(((Type)member).TypeHandle);
                        WriteInt(code, index, token);
                        index += 4;
                        break;
                    default: throw new NotSupportedException("Unexpected controller writer IL operand.");
                }
            }
        }

        private static void WriteInt(byte[] bytes, int offset, int value)
        {
            Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, sizeof(int));
        }

    }
}
