using System.Diagnostics;

namespace SteamVRLibrary;

internal sealed class AboutForm : Form
{
    internal const string Description = "An unofficial program that allows you to unlock certain features in Virtual Desktop Streaming—for example, adding third-party VR games or mods to the streamer's game panel, as well as increasing the bitrate limits for video streaming. Also, set a game launch preset.";

    internal AboutForm()
    {
        Text = "About Virtual Desktop Unlock";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ClientSize = new Size(600, 270);
        BackColor = Color.FromArgb(14, 20, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 4, Padding = new Padding(20)
        };
        var description = new Label
        {
            Text = Description, AutoSize = true, MinimumSize = new Size(560, 0), MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 18)
        };
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(MakeLink("Repository", "https://github.com/ShadowManXR/VirtualDesktop-Unlock"), 0, 1);
        layout.Controls.Add(MakeLink("Donate", "https://ko-fi.com/shadowmanxr"), 0, 2);
        var close = new Button { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.Cancel, Margin = new Padding(0, 14, 0, 0) };
        layout.Controls.Add(close, 0, 3);
        Controls.Add(layout);
        AcceptButton = CancelButton = close;
        Localizer.Apply(this);
    }

    private LinkLabel MakeLink(string text, string url)
    {
        var link = new LinkLabel
        {
            Text = text, Tag = url, AutoSize = true, LinkColor = Color.FromArgb(108, 218, 188),
            ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(108, 218, 188),
            Margin = new Padding(0, 0, 0, 10)
        };
        link.LinkClicked += (_, _) =>
        {
            try { using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { LocalizedMessageBox.Show(this, error, "Could not open link", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        return link;
    }
}
