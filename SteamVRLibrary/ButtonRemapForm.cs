using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal sealed class ButtonRemapForm : Form
{
    private readonly string directory;
    private readonly uint appId;
    private readonly TableLayoutPanel swaps = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = ButtonRemapProfile.MaxSwaps };
    private readonly List<(TableLayoutPanel Panel, ComboBox First, ComboBox Second)> rows = [];
    private readonly Button add = new() { Text = "+", Width = 40, Height = 34, FlatStyle = FlatStyle.Flat };

    internal ButtonRemapForm(Game game, string directory)
    {
        this.directory = directory;
        if (!uint.TryParse(game.Id, out appId) || appId == 0)
            throw new InvalidDataException("Invalid Steam app ID.");
        var profile = ButtonRemapProfile.Read(directory, appId);

        Text = Localizer.T("Remap Buttons");
        BackColor = Color.FromArgb(14, 20, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(600, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        RightToLeft = Localizer.IsRtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = Localizer.IsRtl;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(20) };
        foreach (var height in new[] { 43, 50, 38 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = game.Name, AutoEllipsis = true, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 16, FontStyle.Bold)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = Localizer.T("Experimental. Select VDXR in Streamer and launch the game from Virtual Desktop’s Games tab on Quest 3."),
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(159, 176, 199)
        }, 0, 1);
        add.AccessibleName = Localizer.T("Add swap");
        add.Click += (_, _) => AddRow();
        layout.Controls.Add(add, 0, 2);

        swaps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < ButtonRemapProfile.MaxSwaps; i++)
            swaps.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(swaps);
        layout.Controls.Add(scroll, 0, 3);
        foreach (var swap in profile.Swaps) AddRow(swap);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button
        {
            Text = Localizer.T("Save preset"), AutoSize = true, Height = 36, Padding = new Padding(12, 0, 12, 0),
            BackColor = Color.FromArgb(108, 218, 188), ForeColor = BackColor, FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button { Text = Localizer.T("Cancel"), AutoSize = true, Height = 36, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        footer.Controls.AddRange([save, cancel]);
        layout.Controls.Add(footer, 0, 4);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static ComboBox ButtonPicker(string label, RemapButton? selected)
    {
        var picker = new ComboBox
        {
            AccessibleName = Localizer.T(label), DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill
        };
        picker.Items.AddRange(Enum.GetValues<RemapButton>().Cast<object>().ToArray());
        picker.FormattingEnabled = true;
        picker.Format += (_, e) => e.Value = Localizer.T(e.ListItem switch
        {
            RemapButton.LThumb => "Left stick click",
            RemapButton.RThumb => "Right stick click",
            RemapButton.LGrip => "Left grip",
            RemapButton.RGrip => "Right grip",
            RemapButton.LTrigger => "Left trigger",
            RemapButton.RTrigger => "Right trigger",
            _ => e.ListItem?.ToString() ?? ""
        });
        if (selected.HasValue) picker.SelectedItem = selected.Value;
        return picker;
    }

    private void AddRow(ButtonSwap? swap = null)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        var first = ButtonPicker("First button", swap?.First);
        var second = ButtonPicker("Second button", swap?.Second);
        var remove = new Button { Text = "−", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, AccessibleName = Localizer.T("Remove swap") };
        panel.Controls.Add(first, 0, 0);
        panel.Controls.Add(second, 1, 0);
        panel.Controls.Add(remove, 2, 0);
        var row = (panel, first, second);
        remove.Click += (_, _) =>
        {
            rows.Remove(row);
            swaps.Controls.Remove(panel);
            panel.Dispose();
            for (int i = 0; i < rows.Count; i++) swaps.SetRow(rows[i].Panel, i);
            add.Enabled = true;
            add.Focus();
        };
        swaps.Controls.Add(panel, 0, rows.Count);
        rows.Add(row);
        add.Enabled = rows.Count < ButtonRemapProfile.MaxSwaps;
        if (swap == null) first.Focus();
    }

    private void Save()
    {
        try
        {
            var pairs = new List<ButtonSwap>();
            foreach (var row in rows)
            {
                if (row.First.SelectedItem is not RemapButton first || row.Second.SelectedItem is not RemapButton second)
                    throw new InvalidDataException("Choose two different buttons per swap and use each button only once.");
                pairs.Add(new ButtonSwap(first, second));
            }
            new ButtonRemapProfile(pairs).Save(directory, appId);
            DialogResult = DialogResult.OK;
        }
        catch (Exception error)
        {
            LocalizedMessageBox.Show(this, error, "Could not save button remaps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
