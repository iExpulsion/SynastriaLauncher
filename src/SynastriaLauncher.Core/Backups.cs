using Microsoft.VisualBasic.FileIO;
using SynastriaLauncher.Core.Mods;

namespace SynastriaLauncher.Core;

public sealed record BackupItem(string Path, string Label, long Bytes);

// Backups the launcher leaves behind: old addon versions (AddOns\<name>.bak, when Keep the Previous Version is on),
// settings files a preset replaced (<config>.bak), the files a WowExt update replaced (_WowExtBackups\<version>) and
// Project Reforged parts switched off (Data\_Disabled) and removed addons' saved settings moved aside
// (WTF\_RemovedAddonSettings\<date>), and character settings a copy replaced (WTF\_AltSyncBackups). WowExt.exe.bak is never listed: Restore Original Game needs it.
public static class Backups
{
    public static IReadOnlyList<BackupItem> Find(string gameFolder, IEnumerable<ModDefinition> mods, string dllFolder)
    {
        var addOns = GameFolder.AddOnsPath(gameFolder);
        var addonBackups = Directory.Exists(addOns)
            ? Directory.EnumerateDirectories(addOns, "*.bak")
                .Where(d => new DirectoryInfo(d).LinkTarget is null)
                .Select(d => new BackupItem(d, $"Previous version of {Path.GetFileNameWithoutExtension(d)}", FolderSize(d)))
            : [];
        var configBackups = mods.SelectMany(m => m.Files.Where(f => f.Config))
            .SelectMany(f => new[]
            {
                ModInstaller.ResolvePath(gameFolder, dllFolder, f.Path),
                ModInstaller.ParkedPath(gameFolder, dllFolder, f.Path),
            })
            .OfType<string>()
            .Select(p => p + ".bak")
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new BackupItem(p, $"Replaced {Path.GetFileNameWithoutExtension(p)}", new FileInfo(p).Length));
        var wowExt = Path.Combine(gameFolder, Client.WowExtUpdate.BackupFolderName);
        var wowExtBackups = Directory.Exists(wowExt)
            ? Directory.EnumerateDirectories(wowExt)
                .Select(d => new BackupItem(d, $"WowExt files from {Path.GetFileName(d)}", FolderSize(d)))
            : [];
        var parked = Reforged.ReforgedService.DisabledFolder(gameFolder);
        var reforged = Reforged.ReforgedCatalog.Parts
            .SelectMany(p => p.Files.Select(f => (Part: p, Path: System.IO.Path.Combine(parked, f))))
            .Where(f => File.Exists(f.Path))
            .Select(f => Reforged.ReforgedService.IsLink(f.Path)
                ? new BackupItem(f.Path, $"Link to {Path.GetFileName(f.Path)} for Project Reforged's {f.Part.Name}, switched off (the shared file stays)", 0)
                : new BackupItem(f.Path, $"{Path.GetFileName(f.Path)} for Project Reforged's {f.Part.Name}, switched off", new FileInfo(f.Path).Length));
        var moved = Addons.SavedSettings.MovedPath(gameFolder);
        var movedSettings = Directory.Exists(moved)
            ? Directory.EnumerateDirectories(moved)
                .Select(d => new BackupItem(d, $"Saved settings of removed addons, moved aside {Path.GetFileName(d)}", FolderSize(d)))
            : [];
        var copies = Addons.CharacterSettings.BackupsPath(gameFolder);
        var copyBackups = Directory.Exists(copies)
            ? Directory.EnumerateDirectories(copies)
                .Select(d => new BackupItem(d, $"Character settings before a copy ({Path.GetFileName(d)})", FolderSize(d)))
            : [];
        return addonBackups.Concat(configBackups).Concat(wowExtBackups).Concat(reforged).Concat(movedSettings).Concat(copyBackups)
            .OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static long FolderSize(string folder) =>
        Directory.EnumerateFiles(folder, "*", System.IO.SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    // To the Recycle Bin, so a cleanup can still be undone. A link is just deleted: that removes the link only, never
    // the shared file it points to.
    public static void Recycle(IEnumerable<BackupItem> items)
    {
        foreach (var item in items)
        {
            if (File.Exists(item.Path) && new FileInfo(item.Path).LinkTarget is not null)
            {
                File.Delete(item.Path);
            }
            else if (Directory.Exists(item.Path))
            {
                FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else if (File.Exists(item.Path))
            {
                FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB"
        : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):0} KB"
        : $"{bytes} bytes";
}
