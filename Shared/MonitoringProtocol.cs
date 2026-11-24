using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Xml;

namespace VirtualDesktop.Unlock
{
    public sealed class MonitoringSample
    {
        public DateTime TimestampUtc { get; set; }
        public int ProcessId { get; set; }
        public bool Connected { get; set; }
        public bool Streaming { get; set; }
        public double? Fps { get; set; }
        public double? BitrateMbps { get; set; }
        public int? BatteryPercent { get; set; }
        public bool? Charging { get; set; }
        public double? TotalLatencyMs { get; set; }
        public double? GameRenderMs { get; set; }
        public double? EncodingMs { get; set; }
        public double? NetworkingMs { get; set; }
        public double? DecodingMs { get; set; }
    }

    public static class MonitoringProtocol
    {
        public static string PipeName
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                using (var process = Process.GetCurrentProcess())
                {
                    var user = identity.User;
                    if (user == null) throw new InvalidOperationException("The Windows user identity is unavailable.");
                    return "VDUnlock.Monitoring." + user.Value + "." + process.SessionId;
                }
            }
        }

        public static byte[] Encode(MonitoringSample sample)
        {
            using (var output = new MemoryStream())
            {
                using (var writer = XmlWriter.Create(output, new XmlWriterSettings { CloseOutput = false }))
                {
                    writer.WriteStartElement("Monitoring");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("utc", XmlConvert.ToString(sample.TimestampUtc, XmlDateTimeSerializationMode.Utc));
                    writer.WriteAttributeString("pid", XmlConvert.ToString(sample.ProcessId));
                    writer.WriteAttributeString("connected", XmlConvert.ToString(sample.Connected));
                    writer.WriteAttributeString("streaming", XmlConvert.ToString(sample.Streaming));
                    if (sample.Fps.HasValue) writer.WriteAttributeString("fps", XmlConvert.ToString(sample.Fps.Value));
                    if (sample.BitrateMbps.HasValue) writer.WriteAttributeString("mbps", XmlConvert.ToString(sample.BitrateMbps.Value));
                    if (sample.BatteryPercent.HasValue) writer.WriteAttributeString("battery", XmlConvert.ToString(sample.BatteryPercent.Value));
                    if (sample.Charging.HasValue) writer.WriteAttributeString("charging", XmlConvert.ToString(sample.Charging.Value));
                    WriteTiming(writer, "totalMs", sample.TotalLatencyMs);
                    WriteTiming(writer, "gameMs", sample.GameRenderMs);
                    WriteTiming(writer, "encodeMs", sample.EncodingMs);
                    WriteTiming(writer, "networkMs", sample.NetworkingMs);
                    WriteTiming(writer, "decodeMs", sample.DecodingMs);
                    writer.WriteEndElement();
                }
                return output.ToArray();
            }
        }

        public static MonitoringSample Decode(Stream input)
        {
            using (var reader = XmlReader.Create(input, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096, CloseInput = false }))
            {
                reader.MoveToContent();
                if (reader.Name != "Monitoring" || reader.GetAttribute("version") != "1")
                    throw new InvalidDataException("Invalid monitoring snapshot.");
                var sample = new MonitoringSample
                {
                    TimestampUtc = XmlConvert.ToDateTime(reader.GetAttribute("utc") ?? "", XmlDateTimeSerializationMode.Utc),
                    ProcessId = XmlConvert.ToInt32(reader.GetAttribute("pid") ?? ""),
                    Connected = XmlConvert.ToBoolean(reader.GetAttribute("connected") ?? ""),
                    Streaming = XmlConvert.ToBoolean(reader.GetAttribute("streaming") ?? ""),
                    Fps = Number(reader.GetAttribute("fps") ?? ""),
                    BitrateMbps = Number(reader.GetAttribute("mbps") ?? ""),
                    TotalLatencyMs = Timing(reader.GetAttribute("totalMs") ?? ""),
                    GameRenderMs = Timing(reader.GetAttribute("gameMs") ?? ""),
                    EncodingMs = Timing(reader.GetAttribute("encodeMs") ?? ""),
                    NetworkingMs = Timing(reader.GetAttribute("networkMs") ?? ""),
                    DecodingMs = Timing(reader.GetAttribute("decodeMs") ?? "")
                };
                string battery = reader.GetAttribute("battery") ?? "", charging = reader.GetAttribute("charging") ?? "";
                if (battery.Length != 0) sample.BatteryPercent = XmlConvert.ToInt32(battery);
                if (charging.Length != 0) sample.Charging = XmlConvert.ToBoolean(charging);
                if (sample.ProcessId <= 0 || sample.BatteryPercent < 0 || sample.BatteryPercent > 100 ||
                    sample.Fps > 1000 || sample.BitrateMbps > 100000)
                    throw new InvalidDataException("Monitoring values are outside the supported range.");
                return sample;
            }
        }

        private static void WriteTiming(XmlWriter writer, string name, double? value)
        {
            if (value.HasValue) writer.WriteAttributeString(name, XmlConvert.ToString(value.Value));
        }

        private static double? Timing(string text)
        {
            double? value = Number(text);
            if (value > 10000) throw new InvalidDataException("Invalid monitoring timing.");
            return value;
        }

        private static double? Number(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            double value = Double.Parse(text, CultureInfo.InvariantCulture);
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0) throw new InvalidDataException("Invalid monitoring value.");
            return value;
        }
    }
}
