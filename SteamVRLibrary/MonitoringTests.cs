using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal static class MonitoringTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        DateTime now = DateTime.UtcNow;
        var sample = new MonitoringSample { TimestampUtc = now, ProcessId = 123, Connected = true, Streaming = true,
            Fps = 89.5, BitrateMbps = 222.25, BatteryPercent = 77, Charging = true,
            TotalLatencyMs = 40, GameRenderMs = 5, EncodingMs = 3, NetworkingMs = 6, DecodingMs = 8 };
        using (var input = new MemoryStream(MonitoringProtocol.Encode(sample)))
        {
            var decoded = MonitoringProtocol.Decode(input);
            check(decoded.TimestampUtc == now && decoded.Fps == 89.5 && decoded.BitrateMbps == 222.25 &&
                decoded.BatteryPercent == 77 && decoded.Charging == true,
                "Monitoring protocol preserves timestamp, measured rates, battery and charging status");
            check(decoded.TotalLatencyMs == 40 && decoded.GameRenderMs == 5 && decoded.EncodingMs == 3 && decoded.NetworkingMs == 6 && decoded.DecodingMs == 8,
                "Monitoring protocol preserves all five latency components");
        }
        using (var input = new MemoryStream(MonitoringProtocol.Encode(new MonitoringSample { TimestampUtc = now, ProcessId = 123 })))
        {
            var decoded = MonitoringProtocol.Decode(input);
            check(decoded.Fps == null && decoded.BitrateMbps == null && decoded.BatteryPercent == null && decoded.TotalLatencyMs == null && decoded.NetworkingMs == null && decoded.DecodingMs == null,
                "Unavailable monitoring metrics remain missing instead of becoming zero");
        }
        var history = new MonitoringHistory();
        foreach (var invalid in new[] { "NaN", "-1", "10001", "Infinity" })
        {
            string xml = System.Text.Encoding.UTF8.GetString(MonitoringProtocol.Encode(sample)).Replace("totalMs=\"40\"", "totalMs=\"" + invalid + "\"");
            bool rejected = false;
            try { using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)); MonitoringProtocol.Decode(input); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Monitoring rejects unsafe latency value " + invalid);
        }
        using (var latency = new MonitoringGraph(MonitoringMetric.Latency) { Size = new Size(674, 400) })
        {
            var first = new MonitoringSample { TimestampUtc = now.AddSeconds(-30), TotalLatencyMs = 40, GameRenderMs = 5, EncodingMs = 2, DecodingMs = 7 };
            var second = new MonitoringSample { TimestampUtc = now.AddSeconds(-29), GameRenderMs = 15, DecodingMs = 9 };
            latency.SetData([first, second], now, TimeSpan.FromMinutes(1));
            check(latency.AverageValues.SequenceEqual(new double?[] { 40, 10, 2 }),
                "Latency shows only Total, Game, and Encoding averages and excludes missing values");
            var point = new Point((int)(latency.PlotBounds.Left + latency.PlotBounds.Width * 31 / 60), (int)latency.PlotBounds.Top + 20);
            check(latency.SampleAt(point) == second, "Latency hover works when a component exists without total latency");
        }
        for (int second = 0; second <= 8000; second++)
            history.Add(new MonitoringSample { TimestampUtc = now.AddSeconds(second - 8000), ProcessId = 123,
                Connected = true, Streaming = true, Fps = second % 500 < 10 ? null : 90 + 15 * Math.Sin(second / 18.0),
                BitrateMbps = second % 500 < 10 ? null : 180 + 30 * Math.Sin(second / 31.0),
                BatteryPercent = 95 - second / 180, Charging = false,
                TotalLatencyMs = second % 500 < 10 ? null : 38 + 5 * Math.Sin(second / 27.0),
                GameRenderMs = second % 500 < 10 ? null : 6 + 2 * Math.Sin(second / 18.0),
                EncodingMs = second % 500 < 10 ? null : 3 + Math.Sin(second / 31.0) });
        var retained = history.Read(now);
        check(retained.Length == 7200 && retained[0].TimestampUtc > now - TimeSpan.FromHours(2),
            "Monitoring retains at most 7200 one-second points and drops data older than two hours");
        history.Add(new MonitoringSample { TimestampUtc = now.AddSeconds(-1) });
        check(history.Read(now).Length == 7200, "Duplicate or older monitoring samples do not grow the history");
        using (var graph = new MonitoringGraph(MonitoringMetric.Fps) { Size = new Size(674, 190) })
        {
            var older = new MonitoringSample { TimestampUtc = now.AddSeconds(-90), Fps = 900 };
            var first = new MonitoringSample { TimestampUtc = now.AddSeconds(-30), Fps = 80 };
            var missing = new MonitoringSample { TimestampUtc = now.AddSeconds(-29) };
            var last = new MonitoringSample { TimestampUtc = now.AddSeconds(-28), Fps = 100 };
            graph.SetData([older, first, missing, last], now, TimeSpan.FromMinutes(1));
            check(graph.AverageValue == 90, "Graph average uses only available samples in the selected time range");
            Point At(int seconds) => new((int)(graph.PlotBounds.Left + graph.PlotBounds.Width * (60 + seconds) / 60), (int)graph.PlotBounds.Top + 20);
            check(graph.SampleAt(At(-30)) == first && graph.SampleAt(At(-28)) == last,
                "Graph hover selects the measured sample at the cursor time");
            check(graph.SampleAt(At(-29)) == null && graph.SampleAt(At(-10)) == null && graph.SampleAt(Point.Empty) == null,
                "Graph hover stays empty over missing data, collection gaps, and outside the plot");
            graph.SetData([older, first, missing, last], now, TimeSpan.FromMinutes(5));
            check(graph.AverageValue == 360, "Graph average updates when the time range changes");
            graph.SetData([], now, TimeSpan.FromMinutes(1));
            check(graph.AverageValue == null && graph.SampleAt(At(-30)) == null, "An empty graph has no hover value or average");
        }
        using (var form = new MonitoringForm(history, collect: false))
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Show(); Application.DoEvents();
            IEnumerable<Control> Children(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Children(child)));
            var controls = Children(form).ToArray();
            check(controls.OfType<MonitoringGraph>().Count() == 4 && controls.OfType<Button>().Count() == 4,
                "Monitoring window has four graphs and exactly four time-scale controls");
            foreach (var button in controls.OfType<Button>())
            {
                button.PerformClick();
                check(form.TimeScale == (TimeSpan)button.Tag!, "Monitoring time-scale button selects " + button.Text);
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(root, "monitoring-" + form.TimeScale.TotalMinutes + ".png"));
            }
            form.SelectScale(TimeSpan.FromMinutes(1));
            var fpsGraph = controls.OfType<MonitoringGraph>().First();
            typeof(MonitoringGraph).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(fpsGraph, [new MouseEventArgs(MouseButtons.None, 0, (int)fpsGraph.PlotBounds.Right - 40, (int)fpsGraph.PlotBounds.Top + 40, 0)]);
            using var hoverImage = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(hoverImage, new Rectangle(Point.Empty, form.Size));
            hoverImage.Save(Path.Combine(root, "monitoring-hover.png"));
            var latencyGraph = controls.OfType<MonitoringGraph>().Last();
            typeof(MonitoringGraph).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(latencyGraph, [new MouseEventArgs(MouseButtons.None, 0, (int)latencyGraph.PlotBounds.Right - 40, (int)latencyGraph.PlotBounds.Top + 40, 0)]);
            using var latencyImage = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(latencyImage, new Rectangle(Point.Empty, form.Size));
            latencyImage.Save(Path.Combine(root, "monitoring-latency-hover.png"));
        }
        check(history.Read(now.AddHours(2)).Length == 0, "Monitoring history expires after a long collection gap");
        var sessionHistory = new MonitoringHistory();
        void PumpUntil(Func<bool> ready, int milliseconds)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(20); }
        }
        using (var session = new MonitoringForm(sessionHistory))
        {
            check(sessionHistory.Read(DateTime.UtcNow).Length == 0, "Monitoring does not collect before its window opens");
            session.ShowInTaskbar = false; session.StartPosition = FormStartPosition.Manual;
            session.Location = new Point(-20000, -20000); session.Show();
            PumpUntil(() => sessionHistory.Read(DateTime.UtcNow).Length >= 2, 3500);
            check(sessionHistory.Read(DateTime.UtcNow).Length >= 2, "Opening Monitoring starts one-second collection");
            session.Close();
        }
        int closedCount = sessionHistory.Read(DateTime.UtcNow).Length;
        PumpUntil(() => false, 1400);
        check(sessionHistory.Read(DateTime.UtcNow).Length == closedCount, "Closing Monitoring stops adding samples");
        using (var reopened = new MonitoringForm(sessionHistory))
        {
            reopened.ShowInTaskbar = false; reopened.StartPosition = FormStartPosition.Manual;
            reopened.Location = new Point(-20000, -20000); reopened.Show();
            PumpUntil(() => sessionHistory.Read(DateTime.UtcNow).Length > closedCount, 2500);
            check(sessionHistory.Read(DateTime.UtcNow).Length > closedCount, "Reopening Monitoring resumes collection with retained history");
            reopened.Close();
        }
    }
}
