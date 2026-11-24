using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using VirtualDesktop.Unlock;

namespace VirtualDesktop.LibraryOverrides
{
    internal static class MonitoringRuntime
    {
        private static Delegate originalDelegate;
        private static Action<object, byte[], int, int> originalWrite;
        private static FieldInfo serverField, streamField, inputField, deviceField, headField, batteryField, trackedField;
        private static MethodInfo connectedGetter;
        private static MethodInfo settingsGetter, sourceGetter;
        private static Action<string> log;
        private static object videoStream;
        private static byte[] snapshot;
        private static Timer timer;
        private static Thread pipeThread;
        private static readonly object frameGate = new object();
        private static long payloadBytes, frames, lastFrameTimestamp = Int64.MinValue, lastSampleTicks;
        private static int lastFrameIndex, activeSource = -1, sampling;
        private static int frameSize, frameBytesOffset, frameIndexOffset, frameSourceOffset, sliceSize, sliceBytesOffset;
        private static string lastError;
        private static int processId;
        private static int gameOffset, encodeOffset;
        private static FieldInfo totalLatencyField, inputIndexField;
        private static double gameSum, encodeSum;
        private static int gameCount, encodeCount, lastInputIndex;
        private static long lastInputChange;

        internal static void Install(Assembly assembly, Action<string> logger)
        {
            Initialize(assembly);
            log = logger;
            snapshot = MonitoringProtocol.Encode(new MonitoringSample { TimestampUtc = DateTime.UtcNow, ProcessId = processId });
            lastSampleTicks = Stopwatch.GetTimestamp();
            timer = new Timer(Sample, null, 1000, 1000);
            StartTransport(MonitoringProtocol.PipeName);
            log("Monitoring active; one-second encoded FPS, video payload bitrate, and headset battery samples.");
        }

        internal static void Initialize(Assembly assembly)
        {
            if (originalWrite != null) return;
            Module module = assembly.ManifestModule;
            using (var process = Process.GetCurrentProcess()) processId = process.Id;
            var writeField = module.ResolveField(0x0401653F);
            var invoke = writeField.FieldType.GetMethod("Invoke");
            if (!writeField.IsStatic || invoke.ReturnType != typeof(void) ||
                !invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(object), typeof(byte[]), typeof(int), typeof(int) }))
                throw new NotSupportedException("Monitoring write delegate signature changed.");
            frameSize = Marshal.SizeOf(module.ResolveField(0x04014C8A).DeclaringType);
            sliceSize = Marshal.SizeOf(module.ResolveField(0x04014CFD).DeclaringType);
            frameBytesOffset = Offset(module.ResolveField(0x04014C8A));
            frameIndexOffset = Offset(module.ResolveField(0x04014C7F));
            frameSourceOffset = Offset(module.ResolveField(0x04014C80));
            sliceBytesOffset = Offset(module.ResolveField(0x04014CFD));
            gameOffset = Offset(module.ResolveField(0x04014C84));
            encodeOffset = Offset(module.ResolveField(0x04014C85));
            totalLatencyField = module.ResolveField(0x04014B5D);
            inputIndexField = module.ResolveField(0x04014B5C);
            if (totalLatencyField.FieldType != typeof(float) || inputIndexField.FieldType != typeof(int) ||
                module.ResolveField(0x04014C84).FieldType != typeof(float) || module.ResolveField(0x04014C85).FieldType != typeof(float))
                throw new NotSupportedException("Monitoring timing layout changed.");
            if (frameSize != 160 || sliceSize != 16 || frameBytesOffset != 152 || sliceBytesOffset != 12)
                throw new NotSupportedException("Monitoring packet layout changed.");
            serverField = module.ResolveField(0x040000CD);
            streamField = module.ResolveField(0x04014C98);
            connectedGetter = (MethodInfo)module.ResolveMethod(0x06021427);
            Type sharedSettings = assembly.GetType("VirtualDesktop.Mobile.SharedStreamerSettings", true);
            settingsGetter = sharedSettings.BaseType.GetProperty("Default", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy).GetGetMethod();
            sourceGetter = sharedSettings.GetProperty("StreamingSource").GetGetMethod();
            inputField = module.ResolveField(0x040002F2);
            deviceField = module.ResolveField(0x04014B61);
            headField = module.ResolveField(0x04014A82);
            batteryField = module.ResolveField(0x04014AC2);
            trackedField = module.ResolveField(0x04014AC0);
            if (batteryField.FieldType != typeof(sbyte) || !inputField.IsStatic || !serverField.IsStatic)
                throw new NotSupportedException("Monitoring headset data layout changed.");
            originalDelegate = (Delegate)writeField.GetValue(null);
            var forward = new DynamicMethod("VDUnlockOriginalStreamWrite", typeof(void),
                new[] { typeof(object), typeof(byte[]), typeof(int), typeof(int) }, typeof(MonitoringRuntime), true);
            var il = forward.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, typeof(MonitoringRuntime).GetField("originalDelegate", BindingFlags.NonPublic | BindingFlags.Static));
            il.Emit(OpCodes.Castclass, writeField.FieldType);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Callvirt, invoke); il.Emit(OpCodes.Ret);
            originalWrite = (Action<object, byte[], int, int>)forward.CreateDelegate(typeof(Action<object, byte[], int, int>));
            writeField.SetValue(null, Delegate.CreateDelegate(writeField.FieldType,
                typeof(MonitoringRuntime).GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // Keep the original call outside the observer's exception handler so stream
        // failures retain their original behavior and failed writes add no samples.
        private static void Write(object stream, byte[] bytes, int offset, int count)
        {
            originalWrite(stream, bytes, offset, count);
            if (!Object.ReferenceEquals(stream, Volatile.Read(ref videoStream))) return;
            try { ObservePacket(bytes, offset, count); }
            catch (Exception error) { Interlocked.Exchange(ref lastError, error.Message); }
        }

        internal static void ObservePacket(byte[] bytes, int offset, int count)
        {
            if (count <= 0 || offset < 0 || offset > bytes.Length - count) return;
            if (bytes[offset] == 0 && count >= 1 + frameSize)
            {
                int source = bytes[offset + 1 + frameSourceOffset];
                Volatile.Write(ref activeSource, source);
                if (source != 1) return;
                int size = BitConverter.ToInt32(bytes, offset + 1 + frameBytesOffset);
                if (size < 0 || size != count - 1 - frameSize) return;
                Interlocked.Add(ref payloadBytes, size);
                long timestamp = BitConverter.ToInt64(bytes, offset + 1);
                int index = BitConverter.ToInt32(bytes, offset + 1 + frameIndexOffset);
                lock (frameGate)
                {
                    if (timestamp == lastFrameTimestamp && index == lastFrameIndex) return;
                    lastFrameTimestamp = timestamp;
                    lastFrameIndex = index;
                    Interlocked.Increment(ref frames);
                    float game = BitConverter.ToSingle(bytes, offset + 1 + gameOffset);
                    float encode = BitConverter.ToSingle(bytes, offset + 1 + encodeOffset);
                    if (ValidTiming(game)) { gameSum += game; gameCount++; }
                    if (ValidTiming(encode)) { encodeSum += encode; encodeCount++; }
                }
            }
            else if (bytes[offset] == 1 && Volatile.Read(ref activeSource) == 1 && count >= 1 + sliceSize)
            {
                int size = BitConverter.ToInt32(bytes, offset + 1 + sliceBytesOffset);
                if (size >= 0 && size == count - 1 - sliceSize) Interlocked.Add(ref payloadBytes, size);
            }
        }

        internal static MonitoringSample Capture(double elapsedSeconds)
        {
            object server = serverField.GetValue(null);
            bool connected = server != null && (bool)connectedGetter.Invoke(server, null);
            object currentStream = connected ? streamField.GetValue(server) : null;
            bool changed = !Object.ReferenceEquals(currentStream, Volatile.Read(ref videoStream));
            Volatile.Write(ref videoStream, currentStream);
            bool streaming = connected && Convert.ToInt32(sourceGetter.Invoke(settingsGetter.Invoke(null, null), null)) == 1;
            long bytes = Interlocked.Exchange(ref payloadBytes, 0), count;
            double? game, encode;
            lock (frameGate)
            {
                count = Interlocked.Exchange(ref frames, 0);
                game = gameCount > 0 ? (double?)(gameSum / gameCount) : null;
                encode = encodeCount > 0 ? (double?)(encodeSum / encodeCount) : null;
                gameSum = encodeSum = 0; gameCount = encodeCount = 0;
            }
            var sample = new MonitoringSample { TimestampUtc = DateTime.UtcNow, ProcessId = processId,
                Connected = connected, Streaming = streaming };
            if (!changed && streaming && elapsedSeconds > 0 && elapsedSeconds <= 3)
            {
                sample.Fps = count / elapsedSeconds;
                sample.BitrateMbps = bytes * 8.0 / elapsedSeconds / 1000000;
                sample.GameRenderMs = game;
                sample.EncodingMs = encode;
            }
            if (changed || !streaming)
            {
                Volatile.Write(ref activeSource, -1);
                lock (frameGate) { lastFrameTimestamp = Int64.MinValue; lastFrameIndex = 0; }
            }
            if (connected)
            {
                object input = inputField.GetValue(null);
                int index = (int)inputIndexField.GetValue(input);
                long ticks = Stopwatch.GetTimestamp();
                if (changed || !streaming) lastInputChange = 0;
                if (lastInputChange == 0 || index != lastInputIndex) { lastInputIndex = index; lastInputChange = ticks; }
                double total = Convert.ToDouble(totalLatencyField.GetValue(input)) * 1000;
                if (!changed && streaming && index > 0 && ValidTiming(total) && total > 0 &&
                    (ticks - lastInputChange) / (double)Stopwatch.Frequency <= 3)
                    sample.TotalLatencyMs = total;
                // Network/decode timings are computed inside the headset client.
                // Its tracking feedback carries total latency, not those components.
                object head = headField.GetValue(deviceField.GetValue(input));
                int raw = Convert.ToInt32(batteryField.GetValue(head));
                if (Convert.ToByte(trackedField.GetValue(head)) != 0 && raw != 0 && raw >= -100 && raw <= 100)
                { sample.BatteryPercent = Math.Abs(raw); sample.Charging = raw < 0; }
            }
            return sample;
        }

        private static void Sample(object state)
        {
            if (Interlocked.Exchange(ref sampling, 1) != 0) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - lastSampleTicks) / (double)Stopwatch.Frequency;
                lastSampleTicks = now;
                Volatile.Write(ref snapshot, MonitoringProtocol.Encode(Capture(elapsed)));
                string error = Interlocked.Exchange(ref lastError, null);
                if (error != null) log("Monitoring packet unavailable: " + error);
            }
            catch (Exception error)
            {
                // Discard a failed interval so a later sample cannot attribute
                // accumulated traffic to only its own one-second interval.
                Interlocked.Exchange(ref payloadBytes, 0);
                Interlocked.Exchange(ref frames, 0);
                Volatile.Write(ref activeSource, -1);
                lock (frameGate) { lastFrameTimestamp = Int64.MinValue; lastFrameIndex = 0; gameSum = encodeSum = 0; gameCount = encodeCount = 0; }
                Volatile.Write(ref snapshot, MonitoringProtocol.Encode(new MonitoringSample { TimestampUtc = DateTime.UtcNow, ProcessId = processId }));
                if (lastError != error.Message) { log("Monitoring sample unavailable: " + error.GetBaseException().Message); lastError = error.Message; }
            }
            finally { Volatile.Write(ref sampling, 0); }
        }

        internal static void StartTransport(string pipeName)
        {
            pipeThread = new Thread(delegate() { Serve(pipeName); }) { IsBackground = true, Name = "VD Unlock monitoring" };
            pipeThread.Start();
        }

        private static void Serve(string pipeName)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var security = new PipeSecurity();
                security.SetOwner(identity.User);
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                            PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security))
                        {
                            pipe.WaitForConnection();
                            byte[] current = Volatile.Read(ref snapshot);
                            pipe.Write(current, 0, current.Length);
                        }
                    }
                    catch (IOException) { Thread.Sleep(250); }
                    catch (Exception error) { log("Monitoring connection unavailable: " + error.Message); Thread.Sleep(1000); }
                }
            }
        }

        private static bool ValidTiming(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 && value <= 10000; }

        private static int Offset(FieldInfo field)
        {
            var method = new DynamicMethod("VDUnlockMonitoringOffset", typeof(int), Type.EmptyTypes, typeof(MonitoringRuntime), true);
            var il = method.GetILGenerator(); var local = il.DeclareLocal(field.DeclaringType);
            il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Ldflda, field); il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Ret);
            return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
        }
    }
}
