using Microsoft.VisualBasic.FileIO;
using SynastriaLauncher.Core.Mods;

namespace SynastriaLauncher.Core;

public sealed record BackupItem(string Path, string Label, long Bytes);

// Backups the launcher leaves behind: old addon versions (AddOns\<name>.bak, when Keep the Previous Version is on),
// settings files a preset replaced (<config>.bak) and the files a WowExt update replaced (_WowExtBackups\<version>). WowExt.exe.bak is never listed: Restore Original Game needs it.
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
        return addonBackups.Concat(configBackups).Concat(wowExtBackups).OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static long FolderSize(string folder) =>
        Directory.EnumerateFiles(folder, "*", System.IO.SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    // To the Recycle Bin, so a cleanup can still be undone.
    public static void Recycle(IEnumerable<BackupItem> items)
    {
        foreach (var item in items)
        {
            if (Directory.Exists(item.Path))
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
