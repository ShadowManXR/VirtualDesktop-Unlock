using Microsoft.Win32;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

public interface IStartupRegistration
{
    bool Enabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Virtual Desktop Unlock";
    private readonly string keyPath;
    private readonly string executable;

    public WindowsStartupRegistration(string? keyPath = null, string? executable = null)
    {
        this.keyPath = keyPath ?? RunKey;
        this.executable = executable ?? Environment.ProcessPath!;
    }

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string Command => "\"" + executable + "\"";

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

public sealed class SettingsForm : Form
{
    public SettingsForm(IStartupRegistration startup, bool showErrors = true, TraySettings? traySettings = null,
        BitratePreferences? bitratePreferences = null, VenturePreferences? venturePreferences = null,
        FoveaSizePreferences? foveaPreferences = null)
    {
        Text = "Settings — Virtual Desktop Unlock";
        BackColor = Color.FromArgb(14, 20, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(680, 514);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 11 };
        foreach (int height in new[] { 40, 32, 44, 48, 48, 48, 48, 34, 42, 44, 26 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "Settings", AutoSize = true, Font = new Font(Font.FontFamily, 19, FontStyle.Bold) }, 0, 0);
        root.Controls.Add(new Label { Text = "Language", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(159, 176, 199) }, 0, 1);
        var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Language", DisplayMember = "Name", ValueMember = "Code", DataSource = Localizer.Languages };
        language.BindingContext = new BindingContext(); language.SelectedValue = Localizer.Code;
        language.SelectedIndexChanged += (_, _) =>
        {
            if(language.SelectedItem is not Localizer.Language selected || selected.Code == Localizer.Code)return;
            try { Localizer.Select(selected.Code); Localizer.Apply(this); }
            catch(Exception e)
            {
                language.SelectedValue=Localizer.Code;
                if(showErrors)LocalizedMessageBox.Show(this,e,"Could not save language setting",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
        };
        root.Controls.Add(language, 0, 2);
        var autoStart = new CheckBox { Text = "Launch automatically when Windows starts", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Launch at Windows startup" };
        try { autoStart.Checked = startup.Enabled; }
        catch (Exception e)
        {
            autoStart.Enabled = false;
            if (showErrors) LocalizedMessageBox.Show(this, e, "Could not read Windows startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        bool updating = false;
        autoStart.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            try { startup.SetEnabled(autoStart.Checked); }
            catch (Exception e)
            {
                updating = true; autoStart.Checked = !autoStart.Checked; updating = false;
                if (showErrors) LocalizedMessageBox.Show(this, e, "Could not save Windows startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        root.Controls.Add(autoStart, 0, 3);
        var tray = new CheckBox { Text = "Show in system tray (close window to tray)", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Show in system tray", Checked = traySettings?.Enabled ?? true };
        bool updatingTray = false;
        tray.CheckedChanged += (_, _) =>
        {
            if (updatingTray || traySettings == null) return;
            try { traySettings.SetEnabled(tray.Checked); }
            catch (Exception e)
            {
                updatingTray = true; tray.Checked = !tray.Checked; updatingTray = false;
                if (showErrors) LocalizedMessageBox.Show(this, e, "Could not save system tray setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        root.Controls.Add(tray, 0, 4);
        var bitrate = new CheckBox { Text = "Enable higher bitrate limits", Enabled = bitratePreferences != null, Checked = bitratePreferences?.Configuration.Enabled ?? false, AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Remove streaming bitrate limits" };
        bool updatingBitrate = false;
        bitrate.CheckedChanged += (_, _) =>
        {
            if (updatingBitrate || bitratePreferences == null) return;
            try { var next = bitratePreferences.Configuration.Copy(); next.Enabled = bitrate.Checked; bitratePreferences.Save(next); }
            catch (Exception e)
            {
                updatingBitrate = true; bitrate.Checked = !bitrate.Checked; updatingBitrate = false;
                if (showErrors) LocalizedMessageBox.Show(this, e, "Could not save bitrate setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        root.Controls.Add(ExperimentalOption(bitrate), 0, 5);
        var venture = new CheckBox
        {
            Text = "Show Venture games in Oculus Games (restart Streamer)",
            AccessibleName = "Show Venture games in Oculus Games",
            Enabled = venturePreferences != null,
            Checked = venturePreferences?.Enabled == true,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        bool updatingVenture = false;
        venture.CheckedChanged += (_, _) =>
        {
            if (updatingVenture || venturePreferences == null) return;
            try { venturePreferences.SetEnabled(venture.Checked); }
            catch (Exception e)
            {
                updatingVenture = true;
                venture.Checked = venturePreferences.Enabled;
                updatingVenture = false;
                if (showErrors) LocalizedMessageBox.Show(this, e, "Could not save Venture setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        root.Controls.Add(ExperimentalOption(venture), 0, 6);
        int? savedFoveaSize = null;
        try { savedFoveaSize = foveaPreferences?.Read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Current.Error("Could not read fovea size", error);
        }
        int initialFoveaSize = savedFoveaSize ?? FoveaSizePreferences.Default;
        var fovea = new NumericUpDown
        {
            AccessibleName = "Fovea Size", DecimalPlaces = 2, Increment = 0.01m, Width = 86,
            Minimum = FoveaSizePreferences.Minimum / 100m, Maximum = FoveaSizePreferences.Maximum / 100m,
            Value = initialFoveaSize / 100m, Enabled = foveaPreferences != null
        };
        var foveaRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        foveaRow.Controls.Add(new Label { Text = "Fovea Size", AutoSize = true, Anchor = AnchorStyles.Left });
        foveaRow.Controls.Add(fovea);
        bool changedFoveaSize = false;
        fovea.ValueChanged += (_, _) => changedFoveaSize = true;
        root.Controls.Add(foveaRow, 0, 7);
        root.Controls.Add(new Label
        {
            Text = "Changes the high-detail region in foveated streaming. Restart Streamer to apply.",
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(159, 176, 199)
        }, 0, 8);
        var close = new Button { Text = "Done", DialogResult = DialogResult.OK, Size = new Size(100, 34), Anchor = AnchorStyles.Right, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(108, 218, 188), ForeColor = BackColor };
        FormClosing += (_, e) =>
        {
            int value = (int)decimal.Round(fovea.Value * 100m);
            if (DialogResult != DialogResult.OK || foveaPreferences == null || !changedFoveaSize || value == savedFoveaSize) return;
            try { foveaPreferences.Save(value); }
            catch (Exception error)
            {
                e.Cancel = true;
                if (showErrors) LocalizedMessageBox.Show(this, error, "Could not save fovea size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        root.Controls.Add(close, 0, 9);
        root.Controls.Add(new Label { Text = "by ShadowManXR", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter, ForeColor = Color.FromArgb(159, 176, 199), Font = new Font(Font.FontFamily, 9), Margin = Padding.Empty }, 0, 10);
        AcceptButton = CancelButton = close;
        Localizer.Apply(this);
    }

    private static Control ExperimentalOption(CheckBox option)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(option, 0, 0);
        row.Controls.Add(new Label
        {
            Text = "* Experimental",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(255, 171, 83)
        }, 1, 0);
        return row;
    }
}

internal sealed class FutureOptionCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        ControlPaint.DrawCheckBox(e.Graphics, new Rectangle(0, (Height - 14) / 2, 14, 14), ButtonState.Inactive);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(22, 0, Width - 22, Height),
            Color.FromArgb(159, 176, 199), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
