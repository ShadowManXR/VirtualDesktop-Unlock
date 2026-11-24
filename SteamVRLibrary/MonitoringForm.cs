using System.Drawing.Drawing2D;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal sealed class MonitoringForm : Form
{
    private readonly MonitoringHistory history;
    private readonly bool collect;
    private MonitoringClient? client;
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(159, 176, 199) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly List<Button> scales = [];
    private readonly List<MonitoringGraph> graphs = [];
    internal TimeSpan TimeScale { get; private set; } = TimeSpan.FromMinutes(1);

    internal MonitoringForm(MonitoringHistory history, bool collect = true)
    {
        this.history = history;
        this.collect = collect;
        Text = Localizer.T("Monitoring");
        BackColor = Color.FromArgb(14, 20, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 860);
        MinimumSize = new Size(720, 720);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (float height in new[] { 20f, 20f, 18f, 42f }) layout.RowStyles.Add(new RowStyle(SizeType.Percent, height));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 12) };
        foreach (var scale in new[] { ("1 minute", 1), ("5 minutes", 5), ("30 minutes", 30), ("2 hours", 120) })
        {
            var button = new Button { Text = Localizer.T(scale.Item1), AutoSize = true, FlatStyle = FlatStyle.Flat,
                Padding = new Padding(10, 5, 10, 5), Tag = TimeSpan.FromMinutes(scale.Item2), Margin = new Padding(0, 0, 8, 0) };
            button.Click += (_, _) => SelectScale((TimeSpan)button.Tag);
            scales.Add(button);
            toolbar.Controls.Add(button);
        }
        layout.Controls.Add(toolbar, 0, 0);
        status.Margin = new Padding(0, 0, 0, 10);
        layout.Controls.Add(status, 0, 1);
        foreach (var metric in Enum.GetValues<MonitoringMetric>())
        {
            var graph = new MonitoringGraph(metric) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            graphs.Add(graph);
            layout.Controls.Add(graph, 0, 2 + (int)metric);
        }
        var note = new Label
        {
            Text = Localizer.T("FPS measures encoded stream frames. Bitrate measures sent video payload. History is kept for up to 2 hours."),
            AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(159, 176, 199), Margin = Padding.Empty
        };
        layout.Controls.Add(note, 0, 6);
        Controls.Add(layout);
        RightToLeft = Localizer.IsRtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = Localizer.IsRtl;
        timer.Tick += (_, _) => RefreshGraphs();
        SelectScale(TimeScale);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (collect) client ??= new MonitoringClient(history);
        timer.Start();
    }

    internal void SelectScale(TimeSpan scale)
    {
        TimeScale = scale;
        foreach (var button in scales)
        {
            bool selected = (TimeSpan)button.Tag! == scale;
            button.BackColor = selected ? Color.FromArgb(108, 218, 188) : Color.FromArgb(23, 32, 47);
            button.ForeColor = selected ? BackColor : Color.White;
        }
        RefreshGraphs();
    }

    private void RefreshGraphs()
    {
        DateTime now = DateTime.UtcNow;
        var data = history.Read(now);
        var latest = data.LastOrDefault();
        status.Text = Localizer.T(latest == null || latest.ProcessId == 0 || now - latest.TimestampUtc > TimeSpan.FromSeconds(3)
            ? "Waiting for Streamer Helper 0.3.4-alpha. Install or update the integration."
            : !latest.Connected ? "Connect Virtual Desktop to begin monitoring."
            : !latest.Streaming ? "Start a VR game to monitor FPS and bitrate. Battery monitoring is active."
            : "Collecting one sample per second.");
        foreach (var graph in graphs) graph.SetData(data, now, TimeScale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            client?.Dispose();
            client = null;
            timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum MonitoringMetric { Fps, Bitrate, Battery, Latency }

internal sealed class MonitoringGraph : Control
{
    private sealed record Series(string Name, Color Color, Func<MonitoringSample, double?> Value);
    private readonly MonitoringMetric metric;
    private readonly Series[] series;
    private MonitoringSample[] samples = [];
    private DateTime now = DateTime.UtcNow;
    private TimeSpan scale = TimeSpan.FromMinutes(1);
    private Point? hoverPosition;
    private int PlotTop => metric == MonitoringMetric.Latency ? 41 + series.Length * 19 : 34;
    internal RectangleF PlotBounds => new(54, PlotTop, Math.Max(1, Width - 74), Math.Max(1, Height - PlotTop - 30));
    private IEnumerable<MonitoringSample> VisibleSamples => samples.Where(sample => sample.TimestampUtc >= now - scale && sample.TimestampUtc <= now);
    internal double? AverageValue => AverageValues[0];
    internal double?[] AverageValues => series.Select(item => VisibleSamples.Select(item.Value).Average()).ToArray();

    internal MonitoringSample? SampleAt(Point position)
    {
        if (!PlotBounds.Contains(position)) return null;
        var time = now - scale + TimeSpan.FromSeconds((position.X - PlotBounds.Left) / PlotBounds.Width * scale.TotalSeconds);
        var nearest = VisibleSamples.MinBy(sample => Math.Abs((sample.TimestampUtc - time).TotalSeconds));
        return nearest != null && Math.Abs((nearest.TimestampUtc - time).TotalSeconds) <= 0.75 &&
            series.Any(item => item.Value(nearest).HasValue) ? nearest : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        hoverPosition = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverPosition = null;
        Invalidate();
    }

    internal MonitoringGraph(MonitoringMetric metric)
    {
        this.metric = metric;
        series = metric switch
        {
            MonitoringMetric.Fps => [new("FPS", Color.FromArgb(108, 218, 188), sample => sample.Fps)],
            MonitoringMetric.Bitrate => [new("Bitrate", Color.FromArgb(105, 177, 255), sample => sample.BitrateMbps)],
            MonitoringMetric.Battery => [new("Battery", Color.FromArgb(255, 192, 105), sample => sample.BatteryPercent)],
            _ => [
                new("Total", Color.FromArgb(108, 218, 188), sample => sample.TotalLatencyMs),
                new("Game render time", Color.FromArgb(105, 177, 255), sample => sample.GameRenderMs),
                new("Encoding time", Color.FromArgb(255, 192, 105), sample => sample.EncodingMs)]
        };
        DoubleBuffered = true;
        BackColor = Color.FromArgb(23, 32, 47);
        ForeColor = Color.White;
        AccessibleName = Localizer.T(metric == MonitoringMetric.Latency ? "Latency (ms)" : series[0].Name);
        ResizeRedraw = true;
    }

    internal void SetData(MonitoringSample[] data, DateTime timestamp, TimeSpan timeScale)
    {
        samples = data;
        now = timestamp;
        scale = timeScale;
        Invalidate();
    }

    private string Unit => metric switch { MonitoringMetric.Fps => " FPS", MonitoringMetric.Bitrate => " Mbps", MonitoringMetric.Latency => " ms", _ => "%" };
    private string Reading(double? value, bool average = false) => value.HasValue
        ? value.Value.ToString(metric == MonitoringMetric.Battery && !average ? "0" : "0.0") + Unit : "—";

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = PlotBounds;
        DateTime start = now - scale;
        var visible = VisibleSamples.ToArray();
        double step = metric == MonitoringMetric.Latency ? 10 : 20;
        double maximum = metric == MonitoringMetric.Battery ? 100 :
            Math.Max(metric == MonitoringMetric.Fps ? 60 : metric == MonitoringMetric.Latency ? 20 : 100,
            Math.Ceiling(visible.SelectMany(sample => series.Select(item => item.Value(sample) ?? 0)).DefaultIfEmpty().Max() / step) * step);
        var latest = samples.LastOrDefault();
        if (latest != null && now - latest.TimestampUtc > TimeSpan.FromSeconds(3)) latest = null;
        var hovered = hoverPosition.HasValue ? SampleAt(hoverPosition.Value) : null;
        var averages = AverageValues;
        int half = Width / 2;
        var leftHeader = new Rectangle(12, 8, Math.Max(1, half - 12), 22);
        var rightHeader = new Rectangle(half, 8, Math.Max(1, half - 12), 22);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (metric == MonitoringMetric.Latency)
        {
            TextRenderer.DrawText(graphics, Localizer.T("Latency (ms)"), Font, leftHeader, ForeColor, flags);
            if (hovered != null)
                TextRenderer.DrawText(graphics, hovered.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"), Font, rightHeader, Color.Red, flags | TextFormatFlags.Right);
            for (int index = 0; index < series.Length; index++)
            {
                var item = series[index];
                var selected = hovered ?? latest;
                string value = Reading(selected == null ? null : item.Value(selected));
                int y = 32 + index * 19;
                TextRenderer.DrawText(graphics, Localizer.T(item.Name) + ": " + value, Font,
                    new Rectangle(12, y, Math.Max(1, half - 12), 19), hovered != null ? Color.Red : item.Color, flags);
                TextRenderer.DrawText(graphics, Localizer.T("Average") + ": " + Reading(averages[index], average: true), Font,
                    new Rectangle(half, y, Math.Max(1, half - 12), 19), item.Color, flags | TextFormatFlags.Right);
            }
        }
        else
        {
            string reading = Reading(latest == null ? null : series[0].Value(latest));
            if (metric == MonitoringMetric.Battery && latest?.BatteryPercent != null && latest.Charging == true)
                reading += " · " + Localizer.T("Charging");
            TextRenderer.DrawText(graphics, Localizer.T(series[0].Name) + "  " + reading, Font, leftHeader, ForeColor, flags);
            TextRenderer.DrawText(graphics, Localizer.T("Average") + ": " + Reading(averages[0], average: true), Font, rightHeader, ForeColor, flags | TextFormatFlags.Right);
        }
        using var grid = new Pen(Color.FromArgb(48, 62, 80));
        for (int index = 0; index <= 4; index++)
        {
            float y = plot.Top + plot.Height * index / 4;
            graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(graphics, (maximum * (4 - index) / 4).ToString("0"), Font,
                new Rectangle(0, (int)y - 8, 47, 18), Color.FromArgb(159, 176, 199), TextFormatFlags.Right);
        }
        PointF Position(MonitoringSample sample, double value) => new(
            plot.Left + (float)((sample.TimestampUtc - start).TotalSeconds / scale.TotalSeconds) * plot.Width,
            plot.Bottom - (float)(Math.Clamp(value / maximum, 0, 1) * plot.Height));
        foreach (var item in series)
        {
            using var line = new Pen(item.Color, 2);
            var segment = new List<PointF>();
            DateTime? previousTime = null;
            void DrawSegment()
            {
                if (segment.Count > 1) graphics.DrawLines(line, segment.ToArray());
                else if (segment.Count == 1) graphics.DrawEllipse(line, segment[0].X - 1, segment[0].Y - 1, 2, 2);
                segment.Clear();
            }
            foreach (var sample in visible)
            {
                double? value = item.Value(sample);
                if (!value.HasValue) { DrawSegment(); previousTime = null; continue; }
                if (previousTime.HasValue && sample.TimestampUtc - previousTime.Value > TimeSpan.FromSeconds(2)) DrawSegment();
                segment.Add(Position(sample, value.Value));
                previousTime = sample.TimestampUtc;
            }
            DrawSegment();
        }
        if (hovered != null)
        {
            float x = Position(hovered, 0).X;
            using var marker = new Pen(Color.Red, 1) { DashStyle = DashStyle.Dash };
            using var dot = new SolidBrush(Color.Red);
            graphics.DrawLine(marker, x, plot.Top, x, plot.Bottom);
            foreach (var item in series)
                if (item.Value(hovered) is { } value)
                {
                    var point = Position(hovered, value);
                    graphics.FillEllipse(dot, point.X - 3, point.Y - 3, 6, 6);
                }
            if (metric != MonitoringMetric.Latency)
            {
                string text = hovered.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + Reading(series[0].Value(hovered));
                var size = TextRenderer.MeasureText(text, Font);
                int labelWidth = Math.Min(size.Width + 8, (int)plot.Width);
                int labelLeft = Math.Clamp((int)x + 10, (int)plot.Left, Math.Max((int)plot.Left, (int)plot.Right - labelWidth));
                var label = new Rectangle(labelLeft, (int)plot.Top + 4, labelWidth, size.Height + 4);
                using var background = new SolidBrush(BackColor);
                graphics.FillRectangle(background, label);
                TextRenderer.DrawText(graphics, text, Font, label, Color.Red, flags | TextFormatFlags.HorizontalCenter);
            }
        }
        for (int index = 0; index < 3; index++)
        {
            var time = start + TimeSpan.FromTicks(scale.Ticks * index / 2);
            float x = plot.Left + plot.Width * index / 2;
            int left = index == 0 ? (int)plot.Left : index == 2 ? (int)plot.Right - 76 : (int)x - 38;
            TextRenderer.DrawText(graphics, time.ToLocalTime().ToString("HH:mm:ss"), Font,
                new Rectangle(left, (int)plot.Bottom + 5, 76, 20), Color.FromArgb(159, 176, 199), TextFormatFlags.HorizontalCenter);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) samples = [];
        base.Dispose(disposing);
    }
}
