using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using VirtualDesktop.Unlock;

internal static class MonitoringHookTest
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type runtime;
    private static Module module;
    private static int checks;
    private sealed class FailedWriteStream : MemoryStream
    {
        internal readonly IOException Failure = new IOException("Owned write failure");
        public override void Write(byte[] buffer, int offset, int count) { throw Failure; }
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static MonitoringSample Capture() { return (MonitoringSample)runtime.GetMethod("Capture", All).Invoke(null, new object[] { 1.0 }); }
    private static void SetBattery(int raw)
    {
        var frameField = module.ResolveField(0x040002F2);
        var deviceField = module.ResolveField(0x04014B61);
        var headField = module.ResolveField(0x04014A82);
        object frame = Activator.CreateInstance(frameField.FieldType), device = Activator.CreateInstance(deviceField.FieldType), head = Activator.CreateInstance(headField.FieldType);
        module.ResolveField(0x04014AC0).SetValue(head, (byte)1);
        module.ResolveField(0x04014AC2).SetValue(head, (sbyte)raw);
        headField.SetValue(device, head); deviceField.SetValue(frame, device); frameField.SetValue(null, frame);
    }
    private static object Settings(Assembly assembly)
    {
        Type type = assembly.GetType("VirtualDesktop.Mobile.SharedStreamerSettings", true);
        object shared = FormatterServices.GetUninitializedObject(type);
        for (Type parent = type; parent != null; parent = parent.BaseType)
        {
            var field = parent.GetField("<Default>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) { field.SetValue(null, shared); return shared; }
        }
        throw new Exception("Settings singleton was not found.");
    }
    private static void SetLatency(float seconds, int index)
    {
        var field = module.ResolveField(0x040002F2);
        object input = field.GetValue(null);
        module.ResolveField(0x04014B5D).SetValue(input, seconds);
        module.ResolveField(0x04014B5C).SetValue(input, index);
        field.SetValue(null, input);
    }
    private static void Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]); module = assembly.ManifestModule;
        runtime = typeof(MonitoringSample).Assembly.GetType("VirtualDesktop.LibraryOverrides.MonitoringRuntime", true);
        runtime.GetMethod("Initialize", All).Invoke(null, new object[] { assembly });
        object shared = Settings(assembly);
        var source = shared.GetType().GetField("_streamingSource", All);
        source.SetValue(shared, Enum.ToObject(source.FieldType, 1));
        var write = (MethodInfo)module.ResolveMethod(0x06021472);
        var sliceWrite = (MethodInfo)module.ResolveMethod(0x06021474);
        var sizeField = module.ResolveField(0x04014C8A);
        var sliceSize = module.ResolveField(0x04014CFD);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using (var tcp = new TcpClient())
            {
                tcp.Connect((IPEndPoint)listener.LocalEndpoint);
                using (var peer = listener.AcceptTcpClient())
                using (var stream = new MemoryStream())
                {
                    object server = FormatterServices.GetUninitializedObject(write.DeclaringType);
                    var gate = module.ResolveField(0x04014C95);
                    gate.SetValue(server, Activator.CreateInstance(gate.FieldType, true));
                    module.ResolveField(0x04014C97).SetValue(server, tcp);
                    module.ResolveField(0x04014C98).SetValue(server, stream);
                    module.ResolveField(0x040000CD).SetValue(null, server);
                    SetBattery(-77);
                    SetLatency(0.04f, 1);
                    var first = Capture();
                    Check(first.Fps == null && first.BatteryPercent == 77 && first.Charging == true,
                        "Monitoring warms up a new stream and decodes headset charging percentage");
                    IntPtr data = Marshal.AllocHGlobal(64);
                    try
                    {
                        byte[] payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray(); Marshal.Copy(payload, 0, data, 64);
                        object metadata = Activator.CreateInstance(sizeField.DeclaringType);
                        sizeField.SetValue(metadata, 64);
                        module.ResolveField(0x04014C80).SetValue(metadata, Enum.ToObject(source.FieldType, 1));
                        for (int index = 0; index < 90; index++)
                        {
                            module.ResolveField(0x04014C7F).SetValue(metadata, index);
                            module.ResolveField(0x04014C7E).SetValue(metadata, (long)index * 10000);
                            module.ResolveField(0x04014C84).SetValue(metadata, 4f + (index % 2) * 2);
                            module.ResolveField(0x04014C85).SetValue(metadata, 2f + (index % 2) * 2);
                            write.Invoke(server, new object[] { metadata, data, false });
                        }
                        var measured = Capture();
                        Check(measured.Fps == 90 && Math.Abs(measured.BitrateMbps.Value - 90 * 64 * 8.0 / 1000000) < 0.000001,
                            "Original successful frame writes produce measured FPS and payload Mbps");
                        Check(stream.Length == 90 * 225, "Monitoring preserves the original transmitted bytes");
                        Check(measured.GameRenderMs == 5 && measured.EncodingMs == 3 && Math.Abs(measured.TotalLatencyMs.Value - 40) < 0.001,
                            "Latency uses per-frame game/encode averages in milliseconds and converts headset seconds to total milliseconds");
                        Check(measured.NetworkingMs == null && measured.DecodingMs == null,
                            "Unreported headset network and decode timings remain unavailable");
                        runtime.GetField("lastInputChange", All).SetValue(null, System.Diagnostics.Stopwatch.GetTimestamp() - 4 * System.Diagnostics.Stopwatch.Frequency);
                        measured = Capture();
                        Check(measured.TotalLatencyMs == null && measured.GameRenderMs == null && measured.EncodingMs == null,
                            "Stale headset feedback and frame-free intervals leave latency gaps");
                        SetLatency(0.05f, 2);
                        measured = Capture();
                        Check(Math.Abs(measured.TotalLatencyMs.Value - 50) < 0.001, "Fresh headset feedback resumes total latency");
                        var callback = runtime.GetMethod("Write", All);
                        using (var failed = new FailedWriteStream())
                        {
                            bool preserved = false;
                            try { callback.Invoke(null, new object[] { failed, new byte[225], 0, 225 }); }
                            catch (TargetInvocationException error) { preserved = Object.ReferenceEquals(error.InnerException, failed.Failure); }
                            Check(preserved && (long)runtime.GetField("payloadBytes", All).GetValue(null) == 0,
                                "Failed writes retain the original exception and contribute no video bytes");
                        }
                        using (var other = new MemoryStream())
                        {
                            byte[] packet = stream.ToArray().Take(225).ToArray();
                            callback.Invoke(null, new object[] { other, packet, 0, packet.Length });
                            measured = Capture();
                            Check(other.Length == 225 && measured.BitrateMbps == 0 && measured.Fps == 0,
                                "Writes to other streams remain unchanged and do not enter the video counters");
                        }
                        write.Invoke(server, new object[] { metadata, data, false });
                        write.Invoke(server, new object[] { metadata, data, false });
                        measured = Capture();
                        Check(measured.Fps == 0 && measured.BitrateMbps > 0, "Repeated frame IDs add payload bytes without inflating FPS");
                        Check(measured.GameRenderMs == null && measured.EncodingMs == null, "Duplicate frames do not add timing samples");
                        module.ResolveField(0x04014C7F).SetValue(metadata, 90);
                        module.ResolveField(0x04014C84).SetValue(metadata, Single.NaN);
                        module.ResolveField(0x04014C85).SetValue(metadata, -1f);
                        write.Invoke(server, new object[] { metadata, data, false });
                        measured = Capture();
                        Check(measured.GameRenderMs == null && measured.EncodingMs == null, "Invalid timing values are excluded from interval averages");
                        module.ResolveField(0x04014C84).SetValue(metadata, 5f);
                        module.ResolveField(0x04014C85).SetValue(metadata, 3f);
                        sizeField.SetValue(metadata, 0);
                        module.ResolveField(0x04014C7F).SetValue(metadata, 91);
                        write.Invoke(server, new object[] { metadata, IntPtr.Zero, false });
                        object slice = Activator.CreateInstance(sliceSize.DeclaringType); sliceSize.SetValue(slice, 64);
                        for (int index = 0; index < 4; index++) sliceWrite.Invoke(server, new object[] { slice, data, false });
                        measured = Capture();
                        Check(measured.Fps == 1 && Math.Abs(measured.BitrateMbps.Value - 4 * 64 * 8.0 / 1000000) < 0.000001,
                            "Foveated and stereo slices aggregate into one frame and all video payload bytes");
                        module.ResolveField(0x04014C80).SetValue(metadata, Enum.ToObject(source.FieldType, 0));
                        sizeField.SetValue(metadata, 64); write.Invoke(server, new object[] { metadata, data, false });
                        sliceWrite.Invoke(server, new object[] { slice, data, false }); measured = Capture();
                        Check(measured.Fps == 0 && measured.BitrateMbps == 0, "Desktop packets are excluded from VR monitoring");
                        SetBattery(55); measured = Capture();
                        Check(measured.BatteryPercent == 55 && measured.Charging == false, "Positive headset battery values report discharging");
                        SetBattery(0); measured = Capture();
                        Check(measured.BatteryPercent == null, "Unknown battery values remain unavailable");
                        source.SetValue(shared, Enum.ToObject(source.FieldType, 0)); measured = Capture();
                        Check(measured.Fps == null && measured.BitrateMbps == null && measured.TotalLatencyMs == null && measured.GameRenderMs == null && measured.EncodingMs == null, "Leaving VR produces a gap instead of stale metrics");
                    }
                    finally { Marshal.FreeHGlobal(data); }
                    module.ResolveField(0x040000CD).SetValue(null, null);
                    var disconnected = Capture();
                    Check(!disconnected.Connected && disconnected.BatteryPercent == null && disconnected.Fps == null,
                        "Disconnect clears all monitoring values");
                }
            }
        }
        finally { listener.Stop(); }
        string pipeName = "VDUnlock.Monitoring.Test." + Guid.NewGuid().ToString("N");
        runtime.GetField("snapshot", All).SetValue(null, MonitoringProtocol.Encode(new MonitoringSample
            { TimestampUtc = DateTime.UtcNow, ProcessId = 123, Fps = 90, BitrateMbps = 222, BatteryPercent = 75, Connected = true, Streaming = true }));
        runtime.GetField("log", All).SetValue(null, new Action<string>(Console.WriteLine));
        runtime.GetMethod("StartTransport", All).Invoke(null, new object[] { pipeName });
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In))
        {
            client.Connect(3000);
            var received = MonitoringProtocol.Decode(client);
            Check(received.Fps == 90 && received.BitrateMbps == 222 && received.BatteryPercent == 75,
                "The user-scoped monitoring pipe transfers a complete telemetry snapshot");
        }
        Console.WriteLine("Monitoring hook checks passed: " + checks);
    }
}
