using System.Collections.Generic;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

public sealed class BitrateProfilesForm : Form
{
    private TableLayoutPanel? root;private Label? note;private readonly Size baseClient;
    public BitrateProfilesForm(BitratePreferences preferences)
    {
        Text = "Bitrate profiles — Virtual Desktop Unlock";
        BackColor = Color.FromArgb(14, 20, 31); ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(720, 440); FormBorderStyle = FormBorderStyle.FixedDialog; AutoScroll = true;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var config = preferences.Configuration.Copy();
        root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24), ColumnCount = 2, RowCount = 8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        for (int i = 0; i < 8; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new Label { Text = "Limits by headset and codec", AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold) };
        root.Controls.Add(title, 0, 0); root.SetColumnSpan(title, 2);
        var headsets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Headset bitrate profile", DisplayMember = "Value", ValueMember = "Key", DataSource = BitrateConfiguration.Headsets.ToArray() };
        root.Controls.Add(headsets, 0, 1); root.SetColumnSpan(headsets, 2);
        var custom = new CheckBox { Text = "Use custom limits for this headset", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Custom headset limits" };
        root.Controls.Add(custom, 0, 2); root.SetColumnSpan(custom, 2);
        NumericUpDown Limit(string name, int row)
        {
            root.Controls.Add(new Label { Text = name + " (" + Localizer.T("Mbps") + ")", Dock = DockStyle.Top, AutoSize = true, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var value = new NumericUpDown { Minimum = 10, Maximum = 2000, Increment = 25, ThousandsSeparator = true, Dock = DockStyle.Fill, AccessibleName = name };
            root.Controls.Add(value, 1, row); return value;
        }
        var h264 = Limit("H.264 / H.264+", 3); var hevc = Limit("H.265 / HEVC 10-bit", 4); var av1 = Limit("AV1 / AV1 10-bit", 5);
        note = new Label { Text = "Unconfigured headsets keep stock limits. The bitrate slider and automatic network limiting still apply.", Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.FromArgb(159, 176, 199) };
        root.Controls.Add(note, 0, 6); root.SetColumnSpan(note, 2);
        int previous = -1; bool loading = false;
        void Commit()
        {
            if (previous < 0) return;
            if (custom.Checked) config.Profiles[previous] = new BitrateProfile((int)h264.Value, (int)hevc.Value, (int)av1.Value);
            else config.Profiles.Remove(previous);
        }
        void LoadHeadset()
        {
            if (loading || headsets.SelectedItem is not KeyValuePair<int, string> selected) return;
            Commit(); previous = selected.Key; loading = true;
            var exists = config.Profiles.TryGetValue(previous, out var p); p ??= new BitrateProfile(1000, 300, 250);
            custom.Checked = exists; h264.Value = p.H264Mbps; hevc.Value = p.HevcMbps; av1.Value = p.Av1Mbps;
            h264.Enabled = hevc.Enabled = av1.Enabled = exists; loading = false;
        }
        custom.CheckedChanged += (_, _) => { if (!loading) h264.Enabled = hevc.Enabled = av1.Enabled = custom.Checked; };
        headsets.SelectedIndexChanged += (_, _) => LoadHeadset();
        headsets.BindingContext = new BindingContext();
        headsets.SelectedValue = 1000; LoadHeadset();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Top, AutoSize = true };
        var save = new Button { Text = "Save profiles", Dock = DockStyle.Top, AutoSize = true, BackColor = Color.FromArgb(108, 218, 188), ForeColor = BackColor, FlatStyle = FlatStyle.Flat, AccessibleName = "Save bitrate profiles" };
        save.Click += (_, _) =>
        {
            try { Commit(); preferences.Save(config); DialogResult = DialogResult.OK; Close(); }
            catch (Exception e) { LocalizedMessageBox.Show(this, e.Message, "Could not save bitrate profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        root.Controls.Add(cancel, 0, 7); root.Controls.Add(save, 1, 7);
        AcceptButton = save; CancelButton = cancel;
        Localizer.Apply(this);
        baseClient = ClientSize;
        Shown += (_, _) => ReconcileLayout();
    }
    // Drops stale TableLayoutPanel per-control measurements: a Dock round-trip
    // (None -> saved) is visually a no-op but forces the grid to re-measure
    // rows with the scaled fonts. Needed after DPI transitions and on first
    // show when the process started on a high-DPI screen.
    void ReconcileLayout()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var grids = new System.Collections.Generic.List<TableLayoutPanel>();
        void Walk(Control x)
        {
            if (x is TableLayoutPanel t) grids.Add(t);
            foreach (Control ch in x.Controls) Walk(ch);
        }
        Walk(this);
        foreach (var tlp in grids)
        {
            tlp.SuspendLayout();
            foreach (Control c in tlp.Controls)
            {
                // NOTE: assigning the same Dock value is a no-op in WinForms,
                // so round-trip through the OPPOSITE value to guarantee a
                // genuine change (only genuine Dock changes drop the stale
                // per-control measurements). Visually a no-op.
                var d = c.Dock;
                var a = c.Anchor;
                c.Dock = d == DockStyle.None ? DockStyle.Fill : DockStyle.None;
                c.Dock = d;
                c.Anchor = a;
            }
            tlp.ResumeLayout(true);
        }
        FitContent();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Base first: scale fonts/bounds. The TableLayoutPanel rows go stale
        // across the transition (children keep their old Y positions while
        // fonts grow -> overlapping rows). Reconcile once processed.
        base.OnDpiChanged(e);
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            ReconcileLayout();
        }));
    }
    void FitContent()
    {
        if (root is null || !IsHandleCreated) return;
        var area = Screen.FromHandle(Handle).WorkingArea;
        int maxW = (int)(area.Width * 0.94), maxH = (int)(area.Height * 0.92);
        var pref = root.GetPreferredSize(new Size(Math.Max(ClientSize.Width, baseClient.Width), 0));
        int w = Math.Min(Math.Max(pref.Width, baseClient.Width), maxW);
        ClientSize = new Size(w, ClientSize.Height);
        int avail = Math.Max(200, ClientSize.Width - root.Padding.Horizontal);
        foreach (var lbl in root.Controls.OfType<Label>())
            lbl.MaximumSize = new Size(root.GetColumnSpan(lbl) > 1 ? avail : (int)(avail * 0.55), 0);
        var pref2 = root.GetPreferredSize(new Size(ClientSize.Width, 0));
        int h = Math.Min(Math.Max(pref2.Height, baseClient.Height), maxH);
        ClientSize = new Size(w, h);
    }
}
