using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal sealed class CustomGames(string path)
{
    internal string Path { get; } = path;
    internal List<CustomGameEntry> Entries { get; private set; } = [];
    private string fingerprint = "";

    internal void Reload()
    {
        var before = CustomGameCatalog.Fingerprint(Path);
        var entries = CustomGameCatalog.Read(Path);
        if (before != CustomGameCatalog.Fingerprint(Path)) throw new IOException("The custom game catalog changed. Refresh and try again.");
        Entries = entries;
        fingerprint = before;
    }

    internal IEnumerable<Game> Games => Entries.Select(entry => new Game(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        entry.Name, System.IO.Path.GetDirectoryName(entry.Executable)!, "Third-party program", entry.Executable, entry.Cover));

    internal Game Add(string executable, string name, string? cover, IEnumerable<string> existingIds)
    {
        executable = System.IO.Path.GetFullPath(executable);
        if (!File.Exists(executable) || !System.IO.Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Localizer.T("Choose an existing VR executable (.exe)."));
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException(Localizer.T("Enter a program name."));
        if (Entries.Any(entry => entry.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(Localizer.T("This executable has already been added."));
        var used = existingIds.Concat(Entries.Select(entry => entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToHashSet();
        uint id;
        do { id = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray()) | 0x80000000; }
        while (used.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "CustomCovers");
        Directory.CreateDirectory(directory);
        string artwork = System.IO.Path.Combine(directory, id + ".jpg");
        try
        {
            CreateCover(executable, name.Trim(), cover, artwork);
            var entry = new CustomGameEntry { Id = id, Name = name.Trim(), Executable = executable, Cover = artwork };
            CustomGameCatalog.Save(Path, Entries.Append(entry), fingerprint);
            Reload();
            return Games.Single(game => game.Id == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { if (File.Exists(artwork)) File.Delete(artwork); throw; }
    }

    internal void Delete(IEnumerable<string> ids)
    {
        var removed = ids.ToHashSet();
        var deleted = Entries.Where(entry => removed.Contains(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        CustomGameCatalog.Save(Path, Entries.Where(entry => !removed.Contains(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))), fingerprint);
        Reload();
        foreach (var entry in deleted)
        {
            string ownedCover = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "CustomCovers", entry.Id + ".jpg");
            if (!string.Equals(entry.Cover, ownedCover, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(ownedCover); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { AppLog.Current.Warning("Could not remove a custom game's cover: " + error.Message); }
        }
    }

    internal void SyncSelection(LibraryState state)
    {
        var selected = state.Games.Where(game => game.IsCustom).Select(game => game.Id).ToHashSet();
        if (!Entries.Any(entry => entry.Selected != selected.Contains(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))) return;
        var next = Entries.Select(entry => new CustomGameEntry { Id = entry.Id, Name = entry.Name,
            Executable = entry.Executable, Cover = entry.Cover, Selected = selected.Contains(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
        CustomGameCatalog.Save(Path, next, fingerprint);
        Reload();
    }

    private static void CreateCover(string executable, string name, string? cover, string output)
    {
        using var bitmap = new Bitmap(616, 353);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(23, 32, 47));
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (!string.IsNullOrWhiteSpace(cover))
        {
            using var image = Image.FromFile(cover);
            double scale = Math.Min(616.0 / image.Width, 353.0 / image.Height);
            float width = (float)(image.Width * scale), height = (float)(image.Height * scale);
            graphics.DrawImage(image, (616 - width) / 2, (353 - height) / 2, width, height);
        }
        else
        {
            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon != null) graphics.DrawIcon(icon, new Rectangle(244, 56, 128, 128));
            using var brush = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisWord };
            var titleBounds = new RectangleF(24, 204, 568, 120);
            int fontSize = 64;
            while (fontSize > 16)
            {
                using var candidate = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(name, candidate, new SizeF(titleBounds.Width, 10000), format);
                if (measured.Width <= titleBounds.Width && measured.Height <= titleBounds.Height) break;
                fontSize -= 2;
            }
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString(name, font, brush, titleBounds, format);
        }
        bitmap.Save(output, ImageFormat.Jpeg);
    }
}
