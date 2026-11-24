namespace SteamVRLibrary;

internal sealed class CustomGameForm : Form
{
    private readonly TextBox executable = new() { Dock = DockStyle.Fill };
    private readonly TextBox name = new() { Dock = DockStyle.Fill };
    private readonly TextBox cover = new() { Dock = DockStyle.Fill };
    private readonly Action<string, string, string?> save;

    internal CustomGameForm(Action<string, string, string?> save)
    {
        this.save = save;
        Text = Localizer.T("Add VR program");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 260);
        BackColor = Color.FromArgb(14, 20, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 3, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(layout, 0, "VR executable", executable, () => Browse(executable, "Programs (*.exe)|*.exe"));
        AddRow(layout, 1, "Program name", name, null);
        AddRow(layout, 2, "Cover art (optional)", cover, () => Browse(cover, "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"));
        var hint = new Label { Text = Localizer.T("Without cover art, the executable icon and program name are used."), AutoSize = true,
            Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12) };
        layout.Controls.Add(hint, 0, 3);
        layout.SetColumnSpan(hint, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var add = new Button { Text = Localizer.T("Add"), AutoSize = true };
        var cancel = new Button { Text = Localizer.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        add.Click += (_, _) => Save();
        buttons.Controls.AddRange([add, cancel]);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout);
        AcceptButton = add;
        CancelButton = cancel;
        executable.TextChanged += (_, _) =>
        {
            if (!File.Exists(executable.Text) || !string.IsNullOrWhiteSpace(name.Text)) return;
            name.Text = Path.GetFileNameWithoutExtension(executable.Text);
            try
            {
                if (System.Diagnostics.FileVersionInfo.GetVersionInfo(executable.Text).ProductName is { Length: > 0 } product)
                    name.Text = product;
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { AppLog.Current.Warning("Could not read the executable product name: " + error.Message); }
        };
        RightToLeft = Localizer.IsRtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = Localizer.IsRtl;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, TextBox field, Action? browse)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = Localizer.T(label), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) }, 0, row);
        field.Margin = new Padding(0, 5, 8, 5);
        layout.Controls.Add(field, 1, row);
        if (browse == null) { layout.SetColumnSpan(field, 2); return; }
        var button = new Button { Text = Localizer.T("Browse…"), AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        button.Click += (_, _) => browse();
        layout.Controls.Add(button, 2, row);
    }

    private void Browse(TextBox field, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.FileName;
    }

    private void Save()
    {
        try
        {
            save(executable.Text.Trim(), name.Text.Trim(), cover.Text.Trim());
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) { LocalizedMessageBox.Show(this, error, "Could not add VR program", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
