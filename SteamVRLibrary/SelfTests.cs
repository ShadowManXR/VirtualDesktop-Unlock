using System.Text.Json;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal static class SelfTests
{
    public static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-results", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "Logs", "app.log");
        AppLog.Current = new AppLog(logPath);
        var checks = new List<string>();
        void Check(bool condition, string name)
        { if (!condition) throw new Exception(name); checks.Add("PASS " + name); }
        try
        {
            var remapDirectory = Path.Combine(root, "ButtonRemaps");
            var remapGame = new Game("952060", "Resident Evil 3", "example");
            var otherRemap = new ButtonRemapProfile([new ButtonSwap(RemapButton.X, RemapButton.Y)]);
            otherRemap.Save(remapDirectory, 2981220);
            void EditRemaps(Action<Button[], ComboBox[]> edit)
            {
                using var dialog = new ButtonRemapForm(remapGame, remapDirectory);
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-20000, -20000);
                dialog.Show();
                IEnumerable<Control> ControlsIn(Control parent) =>
                    parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(ControlsIn(child)));
                var initial = ControlsIn(dialog).ToArray();
                if (initial.OfType<ComboBox>().Count() == 0)
                    initial.OfType<Button>().Single(button => button.Text == "+").PerformClick();
                var controls = ControlsIn(dialog).ToArray();
                edit(controls.OfType<Button>().ToArray(), controls.OfType<ComboBox>().ToArray());
                controls.OfType<Button>().Single(button => button.Text == Localizer.T("Save preset")).PerformClick();
                Check(dialog.DialogResult == DialogResult.OK, "Button remap dialog saves edits");
            }
            EditRemaps((_, boxes) =>
            {
                Check(boxes.Length == 2, "Adding a button remap creates two dropdowns");
                Check(boxes.All(box => box.Items.Count == 10 && box.Items.Contains(RemapButton.LGrip) &&
                    box.Items.Contains(RemapButton.RTrigger) && box.Items.Contains(RemapButton.LThumb)),
                    "Remap dropdowns offer face buttons, stick clicks, grips and triggers");
                boxes[0].SelectedItem = RemapButton.A;
                boxes[1].SelectedItem = RemapButton.B;
            });
            short firstFlags = 0x11, secondFlags = 0x14, touches = 0;
            byte leftTrigger = 0, rightTrigger = 0, leftGrip = 0, rightGrip = 0;
            ButtonRemapProfile.Read(remapDirectory, 952060).Apply(ref firstFlags, ref touches,
                ref leftTrigger, ref rightTrigger, ref leftGrip, ref rightGrip);
            ButtonRemapProfile.Read(remapDirectory, 2981220).Apply(ref secondFlags, ref touches,
                ref leftTrigger, ref rightTrigger, ref leftGrip, ref rightGrip);
            Check(firstFlags == 0x12 && secondFlags == 0x18,
                "Button remaps are isolated per Steam app and preserve unrelated inputs");
            EditRemaps((buttons, boxes) =>
            {
                Check(boxes[0].SelectedItem is RemapButton.A && boxes[1].SelectedItem is RemapButton.B,
                    "Reopening the button remap dialog restores the saved pair");
                buttons.Single(button => button.Text == "−").PerformClick();
            });
            Check(ButtonRemapProfile.Read(remapDirectory, 952060).Swaps.Count == 0 &&
                ButtonRemapProfile.Read(remapDirectory, 2981220).Swaps.Count == 1,
                "Removing a button remap clears only the selected game's configuration");
            var allControls = Enum.GetValues<RemapButton>();
            var fiveSwaps = new ButtonRemapProfile(allControls.Chunk(2).Select(pair => new ButtonSwap(pair[0], pair[1])));
            fiveSwaps.Save(remapDirectory, 952060);
            EditRemaps((buttons, boxes) =>
            {
                Check(boxes.Length == 10 && !buttons.Single(button => button.Text == "+").Enabled,
                    "All five swaps fit in the editor and survive a save and reopen");
            });
            try
            {
                _ = new ButtonRemapProfile([new ButtonSwap(RemapButton.A, RemapButton.B), new ButtonSwap(RemapButton.A, RemapButton.X)]);
                throw new Exception("Overlapping button swaps were accepted");
            }
            catch (InvalidDataException) { Check(true, "Overlapping button swaps are rejected"); }

            AppLog.Current.Error("Test operation", new IOException("Test failure"));
            var log = File.ReadAllText(logPath);
            Check(log.Contains("ERROR Test operation") && log.Contains("System.IO.IOException: Test failure"),
                "App log saves error context and exception details");
            using (var logFile = File.OpenWrite(logPath)) logFile.SetLength(2 * 1024 * 1024);
            AppLog.Current.Warning("Test after rotation");
            Check(File.Exists(logPath + ".1") && File.ReadAllText(logPath).Contains("WARN Test after rotation"),
                "App log retains the previous file and continues writing after rotation");
            var steam = Path.Combine(root, "Steam");
            var secondary = Path.Combine(root, "Second library ü");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(secondary, "steamapps"));
            static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                "// test comment\n\"libraryfolders\" { \"0\" { \"path\" " + Quote(steam) + " } \"1\" { \"path\" " + Quote(secondary) + " } }");
            void Manifest(string library, string id, string name, string dir)
            {
                Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", dir));
                File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_" + id + ".acf"),
                    "\"AppState\" { \"appid\" " + Quote(id) + " \"name\" " + Quote(name) + " \"installdir\" " + Quote(dir) + " }");
            }
            Manifest(steam, "111", "Native VR", "Native");
            Manifest(secondary, "952060", "Resident Evil 3", "RE3");
            Manifest(steam, "228980", "Steamworks Redistributables", "Redistributables");
            File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_333.acf"), "\"AppState\" { \"appid\" \"333\" \"name\" \"Uninstalled\" \"installdir\" \"Missing\" }");
            File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_444.acf"), "\"AppState\" { \"appid\" \"444\" \"name\" \"Escape\" \"installdir\" " + Quote("..\\..\\") + " }");
            var cache = Path.Combine(root, "GameSettings.json");
            var cacheText = "{\"SteamProductInfos\":{\"111\":{\"OpenVRSupport\":true},\"952060\":{\"OpenXRSupport\":false},\"999\":{\"OpenXRSupport\":true}}}";
            File.WriteAllText(cache, cacheText);
            SteamMetadataTests.Run(steam, Check);
            GameApiScanTests.Run(Check);
            var scan = SteamScanner.Scan(steam, cache);
            Check(scan.Warnings.Count == 0 && scan.Games.Count == 2, "Scans multiple Steam libraries; excludes tools, missing installs and escaped paths");
            Check(scan.Games.Single(g => g.Id == "952060").InstallDirectory.Contains("Second library ü"), "Handles spaces, Unicode and VDF backslash escapes");
            Check(scan.CachedVrIds.SetEquals(["111", "999"]), "VR cache uses true support flags, including OpenXR");
            Check(File.ReadAllText(cache) == cacheText, "Steam scanning leaves Virtual Desktop cache unchanged");
            var path = Path.Combine(root, "Data", "library.json");
            var store = new LibraryStore(path);
            Check(store.Load() == null, "First launch detects missing companion library");
            var state = new LibraryState { Games = scan.Games.Where(g => scan.CachedVrIds.Contains(g.Id)).ToList() };
            store.Save(state);
            state.Games.Add(scan.Games.Single(g => g.Id == "952060"));
            store.Save(state);
            var reopened = new LibraryStore(path);
            Check(reopened.Load()!.Games.Any(g => g.Id == "952060"), "Moved RE3 persists across store restart");
            Check(JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(path + ".bak"))!.Games.Count == 1, "Atomic saves retain the prior library as a backup");
            reopened.Save(new LibraryState());
            Check(new LibraryStore(path).Load()!.Games.Count == 0, "An intentionally empty VR library stays empty on restart");
            var other = new LibraryStore(path);
            other.Load(); other.Save(new LibraryState { Games = scan.Games });
            var staleRejected = false;
            try { reopened.Save(new LibraryState()); } catch (IOException) { staleRejected = true; }
            Check(staleRejected && new LibraryStore(path).Load()!.Games.Count == 2, "Rejects stale writes without losing changes from another instance");
            File.WriteAllText(path, "{bad json");
            var corruptRejected = false;
            try { new LibraryStore(path).Load(); } catch (JsonException) { corruptRejected = true; }
            Check(corruptRejected && File.ReadAllText(path) == "{bad json", "Corrupt library is reported and not overwritten");
            var malformed = false;
            try { SteamScanner.Values("\"unclosed"); } catch (InvalidDataException) { malformed = true; }
            Check(malformed, "Malformed Steam manifest fails explicitly");
            var sharedPath = Path.Combine(root, "Shared", "GameOverrides.xml");
            var shared = new SharedOverrides(sharedPath);
            var additions = new LibraryState { Games = [new Game("65540", "Gothic 1 Classic", "unused"), new Game("952060", "Resident Evil 3", "unused", "Imported from Virtual Desktop cache"), new Game("111", "Native VR", "unused", "Imported from Virtual Desktop cache")] };
            shared.Save(additions);
            string[] ReadSharedIds(string file) => System.Xml.Linq.XDocument.Load(file).Root!.Elements("Game").Select(x => x.Attribute("appId")!.Value).ToArray();
            Check(ReadSharedIds(sharedPath).SequenceEqual(["111", "65540", "952060"]), "Exports every selected game regardless of origin or VR metadata");
            shared.Save(new LibraryState());
            Check(ReadSharedIds(sharedPath).Length == 0, "Empty custom selection is exported explicitly");
            Check(ReadSharedIds(sharedPath + ".bak").SequenceEqual(["111", "65540", "952060"]), "Shared override updates retain a backup");
            File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_555.acf"), "\"unclosed");
            var partialScan = SteamScanner.Scan(steam, cache);
            Check(partialScan.Games.Count == 2 && partialScan.Warnings.Count == 1, "One corrupt manifest does not prevent other games from loading");

            var uiPath = Path.Combine(root, "UI", "library.json");
            var streamer = new TestStreamerControl();
            using (var form = new LibraryForm(true, uiPath, streamer))
            {
                form.LoadPreview(scan);
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents();
                IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
                var controls = Descendants(form).ToList();
                var left = controls.OfType<ListView>().Single(x => x.AccessibleName!.StartsWith("Installed"));
                var right = controls.OfType<ListView>().Single(x => x.AccessibleName == "VD cache games");
                var addButton = controls.OfType<Button>().Single(x => x.Text.StartsWith("Add to VR"));
                var removeButton = controls.OfType<Button>().Single(x => x.Text == "←  Remove");
                var search = controls.OfType<TextBox>().Single();
                var banner = controls.OfType<Label>().Single(x => x.AccessibleName == "Virtual Desktop status");
                Check(form.Text.StartsWith("Virtual Desktop Unlock") && controls.OfType<Label>().Any(x => x.Text == "VD cache games")
                    && !controls.Any(x => x.Text.Contains("Steam VR Library")), "Uses the new application and cache panel names without the old label");
                Check(banner.Text.Contains("Streamer: Running") && banner.Text.Contains("Library: Loaded") && banner.BackColor == Color.FromArgb(22, 48, 49), "Running Streamer and loaded library show green status");
                var expectedSteam = File.Exists(Path.Combine(SteamScanner.InstallationPath, "steam.exe")) ? "Steam detected" : "Steam not detected";
                var expectedVenture = File.Exists(VirtualDesktop.Unlock.VenturePreferences.LauncherPath) ? "Venture detected" : "Venture not detected";
                Check(banner.Text.Contains(expectedSteam + "   ·   " + expectedVenture), "The status line shows Steam detection before Venture detection");
                var readyIcon = form.Icon;
                Check(form.CurrentIconState == ConnectionIcon.Ready, "Running Streamer with loaded library uses the white-lock ready icon");
                streamer.IsRunning = false; form.UpdateConnectionStatus();
                Check(banner.Text.Contains("Streamer: Not running") && banner.Text.Contains("Library: Loaded") && banner.BackColor == Color.FromArgb(77, 28, 36), "Stopped Streamer with loaded library shows red status");
                Check(form.CurrentIconState == ConnectionIcon.Stopped && !ReferenceEquals(readyIcon, form.Icon), "Stopping Streamer switches the window and tray icon state to red");
                streamer.IsRunning = true; form.UpdateConnectionStatus();
                Check(form.CurrentIconState == ConnectionIcon.Ready && ReferenceEquals(readyIcon, form.Icon), "Restarting Streamer restores the ready icon");
                var bitrateButton = controls.OfType<Button>().Single(x => x.AccessibleName == "Toggle bitrate limits");
                var bitratePath = Path.Combine(Path.GetDirectoryName(uiPath)!, "BitrateOverrides.xml");
                bitrateButton.PerformClick();
                Check(VirtualDesktop.Unlock.BitrateConfiguration.Read(bitratePath).Enabled && bitrateButton.Text == "Restore bitrate limits", "Main bitrate button persists enable and offers stock restoration");
                bitrateButton.PerformClick();
                Check(!VirtualDesktop.Unlock.BitrateConfiguration.Read(bitratePath).Enabled && bitrateButton.Text == "Remove bitrate limits", "Main bitrate button restores stock limits persistently");
                var restart = controls.OfType<LinkLabel>().Single(x => x.AccessibleName == "Restart Streamer");
                Check(left.Items.Count == 1 && right.Items.Count == 1, "UI seeds only installed cached VR games");
                var presetButton=controls.OfType<Button>().Single(x=>x.Text=="Launch Preset");
                var detailsButton=controls.OfType<Button>().Single(x=>x.Text=="Scan details");
                Check(!detailsButton.Enabled,"Scan details starts disabled without a game selection");
                Check(!presetButton.Enabled,"Launch Preset starts disabled without a right-list selection");
                left.Items[0].Selected=true;
                Check(detailsButton.Enabled,"Scan details accepts a selected Steam game");
                Check(!presetButton.Enabled,"Steam-list selection does not enable Launch Preset");
                right.Items[0].Selected=true;
                Check(detailsButton.Enabled,"Scan details accepts a selected VD cache game");
                Check(presetButton.Enabled,"Exactly one selected VD cache game enables Launch Preset");
                right.Items[0].Selected=false;
                Check(!detailsButton.Enabled,"Clearing the game selection disables Scan details");
                Check(!presetButton.Enabled,"Clearing the VD cache selection disables Launch Preset");
                Check(right.Items[0].ForeColor == Color.FromArgb(105,177,255) && left.Items[0].ForeColor == left.ForeColor,
                    "Only VD cache rows backed by original Steam VR metadata appear blue");
                left.Items[0].Selected = true; addButton.PerformClick();
                Check(left.Items.Count == 0 && right.Items.Count == 2 && new LibraryStore(uiPath).Load()!.Games.Count == 2, "Add button moves RE3 and persists the selection");
                Check(right.Items.Cast<ListViewItem>().Single(x => ((Game)x.Tag!).Id == "952060").ForeColor == Color.FromArgb(255,192,105),
                    "A selected game absent from the VD cache appears orange");
                foreach(ListViewItem item in right.Items)item.Selected=true;
                Check(!presetButton.Enabled,"Multiple VD cache selections disable Launch Preset");
                foreach(ListViewItem item in right.Items)item.Selected=false;
                form.LoadPreview(scan with { CachedVrIds = scan.CachedVrIds.Append("952060").ToHashSet() });
                Check(right.Items.Cast<ListViewItem>().Single(x => ((Game)x.Tag!).Id == "952060").ForeColor == right.ForeColor,
                    "A custom game returns to normal text once VD caches it");
                form.LoadPreview(scan with { CachedVrIds = [] });
                Check(right.Items.Cast<ListViewItem>().All(x => x.ForeColor == Color.FromArgb(255,192,105)),
                    "Missing VD cache entry takes orange priority over native Steam VR metadata");
                form.LoadPreview(scan);
                var uiShared = Path.Combine(Path.GetDirectoryName(uiPath)!, "GameOverrides.xml");
                Check(ReadSharedIds(uiShared).SequenceEqual(["111", "952060"]), "Add button syncs custom game IDs for the startup helper");
                Check(restart.Visible && restart.Enabled, "Unapplied selection shows a clickable restart message");
                using (var image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(root, "updated-ui.png"));
                }
                streamer.Fail = true;
                form.RestartStreamerAsync().GetAwaiter().GetResult();
                Check(restart.Visible && restart.Enabled && streamer.Restarts == 1, "Failed restart preserves the message and allows retry");
                streamer.Fail = false;
                form.RestartStreamerAsync().GetAwaiter().GetResult();
                Check(!restart.Visible && streamer.Restarts == 2, "Confirmed restart hides the message");
                right.Items.Cast<ListViewItem>().Single(x => ((Game)x.Tag!).Id == "952060").Selected = true;
                removeButton.PerformClick();
                Check(left.Items.Count == 1 && right.Items.Count == 1 && new LibraryStore(uiPath).Load()!.Games.Count == 1, "Remove button moves RE3 back and saves");
                Check(ReadSharedIds(uiShared).SequenceEqual(["111"]), "Remove button removes the custom helper override");
                Check(restart.Visible, "Removal also requires a Streamer restart");
                left.Items.Cast<ListViewItem>().Single(x => ((Game)x.Tag!).Id == "952060").Selected = true;
                addButton.PerformClick();
                search.Text = "952060";
                Check(right.Items.Count == 1 && ((Game)right.Items[0].Tag!).Id == "952060", "Search filters by Steam ID");
                search.Clear();
                foreach (ListViewItem item in right.Items) item.Selected = true;
                removeButton.PerformClick();
                Check(left.Items.Count == 2 && right.Items.Count == 0 && new LibraryStore(uiPath).Load()!.Games.Count == 0, "Multiple selections can empty the VR library persistently");
                Check(left.Items.Cast<ListViewItem>().All(x => x.ForeColor == left.ForeColor), "Steam list stays normal even for native VR games");
                form.Hide();
                form.WindowState = FormWindowState.Minimized;
                form.Location = new Point(-20000, -20000);
                form.RestoreAndActivate();
                Application.DoEvents();
                Check(form.Visible && form.WindowState == FormWindowState.Normal && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(form.Bounds)),
                    $"Restores a hidden, minimized, off-screen window onto an available display (visible={form.Visible}, state={form.WindowState}, bounds={form.Bounds})");
                using (var activation = new SingleInstance(form))
                {
                    var request = Task.Run(SingleInstance.ActivateExisting);
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (!request.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                    Check(request.IsCompletedSuccessfully && request.Result && form.Visible,
                        "Second-instance activation reaches the existing UI and receives acknowledgement");
                }
            }
            using (var unloaded = new LibraryForm(true, Path.Combine(root, "Unloaded", "library.json"), streamer))
            {
                var banner = unloaded.Controls[0].Controls.OfType<Label>().Single(x => x.AccessibleName == "Virtual Desktop status");
                streamer.IsRunning = false; unloaded.UpdateConnectionStatus();
                Check(banner.Text.Contains("Streamer: Not running") && banner.Text.Contains("Library: Not loaded") && banner.BackColor == Color.FromArgb(83, 52, 18), "Stopped Streamer and unloaded library show orange status");
                Check(unloaded.CurrentIconState == ConnectionIcon.Unloaded, "Unloaded library uses orange even when Streamer is stopped");
                streamer.IsRunning = true; unloaded.UpdateConnectionStatus();
                Check(banner.Text.Contains("Streamer: Running") && banner.Text.Contains("Library: Not loaded") && banner.BackColor == Color.FromArgb(83, 52, 18), "Running Streamer while library is not loaded shows orange status");
                Check(unloaded.CurrentIconState == ConnectionIcon.Unloaded, "Running Streamer retains orange until the library loads");
                unloaded.LoadPreview(scan);
                Check(unloaded.CurrentIconState == ConnectionIcon.Ready, "Loading the library switches orange back to the ready icon");
            }
            File.WriteAllLines(Path.Combine(root, "results.txt"), checks);
            var startup = new TestStartupRegistration();
            var bitratePreferences = new BitratePreferences(Path.Combine(root, "Bitrates", "BitrateOverrides.xml"));
            using (var profiles = new BitrateProfilesForm(bitratePreferences))
            {
                profiles.ShowInTaskbar = false; profiles.StartPosition = FormStartPosition.Manual; profiles.Location = new Point(-20000, -20000); profiles.Show(); Application.DoEvents();
                var fields = profiles.Controls[0].Controls.Cast<Control>().ToArray();
                var headset = fields.OfType<ComboBox>().Single();
                var values = fields.OfType<NumericUpDown>().ToDictionary(x => x.AccessibleName!);
                Check((int)headset.SelectedValue! == 1000 && values["H.264 / H.264+"].Value == 800 && values["H.265 / HEVC 10-bit"].Value == 222 && values["AV1 / AV1 10-bit"].Value == 222,
                    "Profiles window loads the Quest 3 defaults of 800, 222 and 222 Mbps");
                values["AV1 / AV1 10-bit"].Value = 260;
                headset.SelectedValue = 1001;
                fields.OfType<CheckBox>().Single().Checked = true;
                values["H.264 / H.264+"].Value = 800; values["H.265 / HEVC 10-bit"].Value = 275; values["AV1 / AV1 10-bit"].Value = 240;
                using (var image = new Bitmap(profiles.Width, profiles.Height)) { profiles.DrawToBitmap(image, new Rectangle(Point.Empty, profiles.Size)); image.Save(Path.Combine(root, "bitrate-profiles-ui.png")); }
                fields.OfType<Button>().Single(x => x.AccessibleName == "Save bitrate profiles").PerformClick();
                var saved = VirtualDesktop.Unlock.BitrateConfiguration.Read(bitratePreferences.Path);
                Check(saved.Profiles[1000].Av1Mbps == 260 && saved.Profiles[1001].Av1Mbps == 240 && saved.Profiles[1001].HevcMbps == 275 && !saved.Enabled, "Headset/codec edits persist separately without enabling the feature implicitly");
            }
            var trayPath = Path.Combine(root, "Preferences", "Settings.json");
            var traySettings = new TraySettings(trayPath);
            var venturePath = Path.Combine(root, "Preferences", "Venture.enabled");
            var venturePreferences = new VirtualDesktop.Unlock.VenturePreferences(venturePath);
            Check(traySettings.Enabled && !File.Exists(trayPath), "System tray is enabled by default without overwriting settings");
            using (var settings = new SettingsForm(startup, showErrors: false, traySettings: traySettings,
                bitratePreferences: bitratePreferences, venturePreferences: venturePreferences))
            {
                settings.ShowInTaskbar = false; settings.StartPosition = FormStartPosition.Manual;
                settings.Location = new Point(-20000, -20000); settings.Show(); Application.DoEvents();
                var settingsControls = settings.Controls[0].Controls.Cast<Control>().ToArray();
                var experimentalRows = settingsControls.OfType<TableLayoutPanel>().ToArray();
                var experimentalOptions = experimentalRows.SelectMany(row => row.Controls.OfType<CheckBox>()).ToArray();
                Check(experimentalRows.Length == 2 && experimentalRows.All(row =>
                    row.Controls.OfType<Label>().Any(label => label.Text == "* Experimental" && label.ForeColor == Color.FromArgb(255, 171, 83))),
                    "Venture and bitrate settings have orange Experimental labels");
                var venture = experimentalOptions.Single(c => c.AccessibleName == "Show Venture games in Oculus Games");
                Check(venture.Enabled && !venture.Checked && !File.Exists(venturePath), "Venture starts unchecked without creating a preference");
                venture.Checked = true;
                Check(new VirtualDesktop.Unlock.VenturePreferences(venturePath).Enabled, "Enabling Venture persists across settings reload");
                venture.Checked = false;
                Check(!new VirtualDesktop.Unlock.VenturePreferences(venturePath).Enabled, "Disabling Venture persists across settings reload");
                var autoStart = settingsControls.OfType<CheckBox>().Single(c => c.AccessibleName == "Launch at Windows startup");
                var tray = settingsControls.OfType<CheckBox>().Single(c => c.AccessibleName == "Show in system tray");
                Check(tray.Checked, "Settings shows the enabled-by-default system tray option");
                tray.Checked = false;
                Check(!traySettings.Enabled && !new TraySettings(trayPath).Enabled, "Disabling the system tray persists across settings reload");
                tray.Checked = true;
                Check(new TraySettings(trayPath).Enabled, "Re-enabling the system tray persists across settings reload");
                Check(!autoStart.Checked && startup.Writes == 0, "Opening settings leaves Windows startup unchanged");
                Check(settingsControls.OfType<ComboBox>().Single().Enabled && settingsControls.OfType<ComboBox>().Single().Items.Count == 10, "Language picker offers all ten supported languages");
                var bitrateSetting = experimentalOptions.Single(c => c.AccessibleName == "Remove streaming bitrate limits");
                Check(bitrateSetting.Enabled, "Bitrate setting is functional rather than a future placeholder");
                bitrateSetting.Checked = true;
                Check(VirtualDesktop.Unlock.BitrateConfiguration.Read(bitratePreferences.Path).Enabled, "Settings checkbox saves the same bitrate configuration");
                bitrateSetting.Checked = false;
                autoStart.Checked = true;
                Check(startup.Enabled && startup.Writes == 1, "Startup checkbox enables automatic launch");
                startup.Fail = true; autoStart.Checked = false;
                Check(autoStart.Checked && startup.Enabled, "Failed startup write restores the checkbox state");
                startup.Fail = false; autoStart.Checked = false;
                Check(!startup.Enabled, "Startup checkbox disables automatic launch");
                autoStart.Checked = true;
                using var image = new Bitmap(settings.Width, settings.Height);
                settings.DrawToBitmap(image, new Rectangle(Point.Empty, settings.Size));
                image.Save(Path.Combine(root, "settings-ui.png"));
            }
            using (var settings = new SettingsForm(startup, showErrors: false))
            {
                Check(settings.Controls[0].Controls.OfType<CheckBox>().Single(c => c.AccessibleName == "Launch at Windows startup").Checked, "Reopened settings reflect persisted startup preference");
            }
            using (var trayForm = new LibraryForm(true, Path.Combine(root, "TrayUI", "library.json"), streamer, traySettings))
            {
                trayForm.LoadPreview(scan); trayForm.ShowInTaskbar = false;
                trayForm.StartPosition = FormStartPosition.Manual; trayForm.Location = new Point(-20000, -20000);
                trayForm.Show(); Application.DoEvents();
                trayForm.Close();
                Check(!trayForm.IsDisposed && !trayForm.Visible && !trayForm.ShowInTaskbar, "Close hides the window to tray while enabled");
                trayForm.RestoreAndActivate(); Application.DoEvents();
                Check(trayForm.Visible && trayForm.ShowInTaskbar, "Tray Open restores the window and taskbar entry");
                trayForm.Close(); traySettings.SetEnabled(false); Application.DoEvents();
                Check(trayForm.Visible && trayForm.ShowInTaskbar, "Disabling tray mode restores a hidden window");
                trayForm.Close();
                Check(trayForm.IsDisposed, "Close exits normally when system tray is disabled");
            }
            traySettings.SetEnabled(true);
            using (var exitForm = new LibraryForm(true, Path.Combine(root, "TrayExit", "library.json"), streamer, traySettings))
            {
                exitForm.ShowInTaskbar = false; exitForm.Show(); Application.DoEvents(); exitForm.ExitApplication();
                Check(exitForm.IsDisposed, "Tray Exit closes the application even when tray mode is enabled");
            }
            LaunchPresetTests.Run(root, Check);
            LocalizationTests.Run(root, Check);
            File.WriteAllLines(Path.Combine(root, "results.txt"), checks);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-result.txt"), string.Join(Environment.NewLine, checks));
            return 0;
        }
        catch (Exception e)
        {
            checks.Add("FAIL " + e);
            File.WriteAllLines(Path.Combine(root, "results.txt"), checks);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-result.txt"), string.Join(Environment.NewLine, checks));
            return 1;
        }
    }

    private sealed class TestStreamerControl : IStreamerControl
    {
        private HashSet<uint> applied = [111];
        private uint[] requested = [];
        public bool Fail { get; set; }
        public bool IsRunning { get; set; } = true;
        public int Restarts { get; private set; }
        public bool IsApplied(IReadOnlyCollection<uint> ids)
        { requested = ids.ToArray(); return applied.SetEquals(ids); }
        public Task RestartAsync()
        {
            Restarts++;
            if (Fail) return Task.FromException(new IOException("Simulated restart failure"));
            applied = requested.ToHashSet();
            return Task.CompletedTask;
        }
    }

    private sealed class TestStartupRegistration : IStartupRegistration
    {
        public bool Enabled { get; private set; }
        public bool Fail { get; set; }
        public int Writes { get; private set; }
        public void SetEnabled(bool enabled)
        {
            if (Fail) throw new IOException("Simulated registry failure");
            Enabled = enabled; Writes++;
        }
    }
}
