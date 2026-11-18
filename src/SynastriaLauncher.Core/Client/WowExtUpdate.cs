using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SynastriaLauncher.Core.GitHub;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Core.Client;

// The newest WowExt package linked from synastria.org (WoWExt_v<version>.zip, usually on MediaFire).
public sealed record WowExtRelease(int Version, string Url);

public enum WowExtFileChange
{
    Same,
    Changed,
    Missing,
}

public sealed record WowExtFile(string Name, WowExtFileChange Change);

public sealed record WowExtPlan(int? Version, IReadOnlyList<WowExtFile> Files)
{
    public IReadOnlyList<WowExtFile> Updates => Files.Where(f => f.Change != WowExtFileChange.Same).ToList();
    public bool IsCurrent => Updates.Count == 0;
    public bool ExeChanged => Updates.Any(f => f.Name.Equals(GameFolder.ExeName, StringComparison.OrdinalIgnoreCase));
}

// Updates Synastria's client files (WowExt.exe, WoWExt.dll and its data files) from the official package. WoWExt.bin
// is the server's cache of what it sent, newer in a played client than in the package, so it's only written when
// missing. Replaced files are kept in _WowExtBackups\<old version>. A new WowExt.exe becomes the backed-up original
// too (what Restore Original Game puts back), and the launcher's exe patches are put back on it.
public static partial class WowExtUpdate
{
    public const string SiteUrl = "https://www.synastria.org/";
    public const string BackupFolderName = "_WowExtBackups";
    public const string CacheFile = "WoWExt.bin";
    private static readonly string[] Required = [GameFolder.ExeName, "WoWExt.dll"];

    [GeneratedRegex("""href\s*=\s*["']([^"']*WoWExt_v(\d+)\.zip[^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageLink();

    [GeneratedRegex("""href\s*=\s*["'](https://download\d*\.mediafire\.com/[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFireDirect();

    [GeneratedRegex(@"WoWExt_v(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionInName();

    public static WowExtRelease? ParseSite(string html) =>
        PackageLink().Matches(html).Select(m => new WowExtRelease(int.Parse(m.Groups[2].Value), System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)))
            .MaxBy(r => r.Version);

    public static string? ParseMediaFire(string html) =>
        MediaFireDirect().Match(html) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    public static int? VersionFromName(string fileName) =>
        VersionInName().Match(fileName) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    public static async Task<WowExtRelease?> CheckAsync(GitHubKeyless http, CancellationToken cancel = default) =>
        await http.GetTextOrNullAsync(SiteUrl, cancel) is { } html ? ParseSite(html) : null;

    // MediaFire's page carries the file's direct link; any other link is taken as the file itself.
    public static async Task DownloadAsync(GitHubKeyless http, WowExtRelease release, string destination, CancellationToken cancel = default)
    {
        var uri = new Uri(release.Url);
        var direct = uri.Host.EndsWith("mediafire.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.StartsWith("download", StringComparison.OrdinalIgnoreCase)
            ? ParseMediaFire(await http.GetTextOrNullAsync(release.Url, cancel) ?? "")
                ?? throw new InvalidDataException("MediaFire's download page has changed, so the file couldn't be found on it. Use Open Download Page instead.")
            : release.Url;
        await http.DownloadAsync(direct, destination, cancel);
    }

    // The package's files by name (folders inside it are ignored), checked for WowExt.exe and WoWExt.dll.
    public static IReadOnlyDictionary<string, byte[]> ReadPackage(string zipPath)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(zipPath);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                using var stream = entry.OpenEntryStream();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                files[Path.GetFileName(entry.Key!.Replace('\\', '/'))] = memory.ToArray();
            }
        }
        catch (Exception e) when (e is not (IOException or InvalidDataException))
        {
            throw new InvalidDataException($"{Path.GetFileName(zipPath)} couldn't be opened as a zip: {e.Message}", e);
        }
        var missing = Required.Where(r => !files.ContainsKey(r)).ToList();
        return missing.Count == 0 ? files
            : throw new InvalidDataException($"This isn't a WowExt package: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} missing from it.");
    }

    // WowExt.exe is compared with the original the launcher keeps (WowExt.exe.bak) when there is one, since the exe
    // itself carries the launcher's patches.
    public static WowExtPlan Plan(IReadOnlyDictionary<string, byte[]> package, string gameFolder, int? version)
    {
        var exe = new GameExe(gameFolder);
        var files = package.Where(p => !p.Key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var isExe = p.Key.Equals(GameFolder.ExeName, StringComparison.OrdinalIgnoreCase);
            var path = isExe && exe.HasBackup ? exe.BackupPath : Path.Combine(gameFolder, p.Key);
            var change = !File.Exists(path) ? WowExtFileChange.Missing
                : p.Key.Equals(CacheFile, StringComparison.OrdinalIgnoreCase) || File.ReadAllBytes(path).AsSpan().SequenceEqual(p.Value) ? WowExtFileChange.Same
                : WowExtFileChange.Changed;
            return new WowExtFile(p.Key, change);
        }).ToList();
        return new WowExtPlan(version, files);
    }

    public static string BackupFolder(string gameFolder, int? oldVersion) =>
        Path.Combine(gameFolder, BackupFolderName, oldVersion is { } v ? $"v{v}" : $"Before {DateTime.Now:yyyy-MM-dd HH.mm}");

    // Writes the plan's files. The old ones go to the backup folder first (for WowExt.exe, the original). Returns the
    // result of putting the patches back on a new exe, or null when the exe didn't change.
    public static StateResult? Apply(WowExtPlan plan, IReadOnlyDictionary<string, byte[]> package, string gameFolder, int? oldVersion,
        byte[]? patcherDll)
    {
        var exe = new GameExe(gameFolder);
        var backup = BackupFolder(gameFolder, oldVersion);
        StateResult? patches = null;
        foreach (var file in plan.Updates)
        {
            var isExe = file.Name.Equals(GameFolder.ExeName, StringComparison.OrdinalIgnoreCase);
            var path = Path.Combine(gameFolder, file.Name);
            var old = isExe && exe.HasBackup ? exe.BackupPath : path;
            if (File.Exists(old))
            {
                Directory.CreateDirectory(backup);
                File.Copy(old, Path.Combine(backup, file.Name), overwrite: true);
            }
            if (isExe && File.Exists(path))
            {
                patches = exe.ReplaceOriginal(package[file.Name], patcherDll);
            }
            else
            {
                File.WriteAllBytes(path, package[file.Name]);
            }
        }
        return patches;
    }
}
