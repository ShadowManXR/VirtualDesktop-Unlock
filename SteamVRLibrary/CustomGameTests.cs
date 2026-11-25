using System.Xml.Linq;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal static class CustomGameTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "CustomGames");
        Directory.CreateDirectory(directory);
        string catalog = Path.Combine(directory, "CustomGames.xml");
        string executable = Environment.ProcessPath!;
        var games = new CustomGames(catalog);
        games.Reload();
        bool missingRejected = false;
        try { games.Add(Path.Combine(directory, "missing.exe"), "Missing", null, []); }
        catch (InvalidDataException) { missingRejected = true; }
        check(missingRejected && games.Entries.Count == 0, "Missing executables are rejected before adding a custom game");
        var fallback = games.Add(executable, "Custom VR test", null, []);
        using (var cover = new Bitmap(fallback.CoverPath!))
        {
            check(cover.Width == 616 && cover.Height == 353 && cover.GetPixel(308, 120).ToArgb() != cover.GetPixel(5, 5).ToArgb(),
                "Custom game without artwork generates a centered executable icon cover");
            check(Enumerable.Range(216, 90).Any(y => Enumerable.Range(40, 536).Any(x =>
                cover.GetPixel(x, y).R > 150 && cover.GetPixel(x, y).G > 150)),
                "Generated custom cover includes the program name below the icon");
        }
        bool duplicateRejected = false;
        try { games.Add(executable.ToUpperInvariant(), "Duplicate", null, []); }
        catch (InvalidDataException) { duplicateRejected = true; }
        check(duplicateRejected && games.Entries.Count == 1, "Duplicate executable paths are rejected without changing the catalog");
        var stale = new CustomGames(catalog);
        stale.Reload();
        games.SyncSelection(new LibraryState { Games = [fallback] });
        bool conflict = false;
        try { stale.Delete([fallback.Id]); }
        catch (IOException) { conflict = true; }
        check(conflict && CustomGameCatalog.Read(catalog).Single().Selected, "A stale catalog writer preserves newer game selections");
        var steamGame = new Game("111", "Steam selection", directory);
        var restored = new LibraryState { Games = [steamGame] };
        check(games.RestoreSelection(restored) && restored.Games.SequenceEqual(new[] { steamGame, fallback }) && CustomGameCatalog.Read(catalog).Single().Selected,
            "Shared custom selections restore into a fresh local library without changing Steam selections or the catalog");
        check(!games.RestoreSelection(restored), "Restoring unchanged custom selections does not require another local save");
        games.SyncSelection(new LibraryState());
        check(games.RestoreSelection(restored) && restored.Games.SequenceEqual(new[] { steamGame }),
            "A stale local library cannot reselect a custom game removed in another build");
        games.Delete([fallback.Id]);
        check(!File.Exists(fallback.CoverPath), "Deleting a custom game removes its generated cover");
        string[] coverTitles = ["VR", "Teenage Mutant Ninja Turtles Empire City",
            "A Very Long Virtual Reality Adventure Across the Stars and Beyond the Edge of the Known Universe"];
        for (int index = 0; index < coverTitles.Length; index++)
        {
            var titleCover = games.Add(executable, coverTitles[index], null, []);
            File.Copy(titleCover.CoverPath!, Path.Combine(root, $"fallback-title-{index}.jpg"));
            games.Delete([titleCover.Id]);
        }
        string artwork = Path.Combine(directory, "source.png");
        using (var image = new Bitmap(200, 100))
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Red);
            image.Save(artwork);
        }
        var covered = games.Add(executable, "Supplied cover", artwork, []);
        using (var cover = new Bitmap(covered.CoverPath!))
            check(cover.GetPixel(308, 176).R > 200 && cover.GetPixel(308, 176).G < 40,
                "Supplied custom artwork is copied into the dashboard cover");
        File.Delete(artwork);
        check(File.Exists(covered.CoverPath), "Custom artwork remains available after the original image is removed");
        games.Delete([covered.Id]);

        using var form = new LibraryForm(true, Path.Combine(directory, "library.json"), new PreviewStreamer());
        var scan = new ScanResult([new Game("111", "Steam test", directory)], [], []);
        form.LoadPreview(scan);
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Application.DoEvents();
        IEnumerable<Control> Children(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Children(child)));
        var controls = Children(form).ToArray();
        var steam = controls.OfType<ListView>().Single(list => list.AccessibleName!.StartsWith("Installed"));
        var vr = controls.OfType<ListView>().Single(list => list.AccessibleName == "VD cache games");
        var delete = controls.OfType<Button>().Single(button => button.Text == "Delete");
        var add = controls.OfType<Button>().Single(button => button.Text.StartsWith("Add to VR"));
        var remove = controls.OfType<Button>().Single(button => button.Text == "←  Remove");
        var custom = form.AddCustomProgram(executable, "Custom VR test", null);
        var item = steam.Items.Cast<ListViewItem>().Single(item => ((Game)item.Tag!).IsCustom);
        check(item.ForeColor == Color.FromArgb(0, 128, 64) && !delete.Visible, "Added executable appears dark green in Steam games with Delete initially hidden");
        item.Selected = true;
        check(delete.Visible && delete.Enabled, "Selecting a third-party program shows the Delete button");
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            image.Save(Path.Combine(root, "custom-programs-ui.png"));
        }
        add.PerformClick();
        check(vr.Items.Count == 1 && CustomGameCatalog.Read(catalog).Single().Selected,
            "Add to VR selects the custom executable for the headset library");
        check(!XDocument.Load(Path.Combine(directory, "GameOverrides.xml")).Descendants("Game").Any(),
            "Custom executable IDs stay out of Steam discovery overrides");
        var saved = new LibraryStore(Path.Combine(directory, "library.json")).Load()!;
        check(saved.Games.Single().ExecutablePath == executable, "Selected custom executable survives library save and reload");
        void CheckReopened(string path, bool selected, string message)
        {
            using var reopened = new LibraryForm(true, path, new PreviewStreamer());
            reopened.LoadPreview(scan);
            var lists = Children(reopened).OfType<ListView>().ToArray();
            var reopenedVr = lists.Single(list => list.AccessibleName == "VD cache games");
            var reopenedSteam = lists.Single(list => list.AccessibleName!.StartsWith("Installed"));
            check(reopenedVr.Items.Cast<ListViewItem>().Any(item => ((Game)item.Tag!).Id == custom.Id) == selected &&
                reopenedSteam.Items.Cast<ListViewItem>().Any(item => ((Game)item.Tag!).Id == custom.Id) != selected &&
                CustomGameCatalog.Read(catalog).Single().Selected == selected, message);
        }
        CheckReopened(Path.Combine(directory, "library.json"), true, "Reopening the same app keeps the selected custom game in VD cache games");
        var otherStore = new LibraryStore(Path.Combine(directory, "other-build-library.json"));
        otherStore.Save(new LibraryState());
        CheckReopened(otherStore.Path, true, "Opening a build with an empty saved library restores custom selections from the shared catalog");
        CheckReopened(Path.Combine(directory, "fresh-build-library.json"), true, "Opening a build without a local library keeps shared custom selections");
        vr.Items[0].Selected = true;
        remove.PerformClick();
        check(!CustomGameCatalog.Read(catalog).Single().Selected && steam.Items.Count == 2,
            "Removing a custom executable from VR returns it to Steam games");
        otherStore.Save(saved);
        CheckReopened(otherStore.Path, false, "Reopening an older build cannot undo an explicit custom game removal");
        form.LoadPreview(scan);
        item = steam.Items.Cast<ListViewItem>().Single(item => ((Game)item.Tag!).IsCustom);
        item.Selected = true;
        delete.PerformClick();
        check(steam.Items.Count == 1 && !delete.Visible && CustomGameCatalog.Read(catalog).Count == 0 && File.Exists(executable),
            "Delete removes the custom catalog entry while preserving the program executable");
        steam.Items[0].Selected = true;
        check(!delete.Visible, "Selecting a Steam game hides Delete");
        using var dialog = new CustomGameForm((_, _, _) => { });
        dialog.ShowInTaskbar = false;
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-20000, -20000);
        dialog.Show();
        Application.DoEvents();
        using var dialogImage = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(dialogImage, new Rectangle(Point.Empty, dialog.Size));
        dialogImage.Save(Path.Combine(root, "custom-program-dialog.png"));
    }

    private sealed class PreviewStreamer : IStreamerControl
    {
        public bool IsRunning => true;
        public bool IntegrationInstalled => true;
        public bool IsApplied(IReadOnlyCollection<uint> ids) => true;
        public bool IsCustomGamesApplied(string fingerprint) => true;
        public Task RestartAsync() => Task.CompletedTask;
        public Task InstallIntegrationAsync() => Task.CompletedTask;
    }
}
