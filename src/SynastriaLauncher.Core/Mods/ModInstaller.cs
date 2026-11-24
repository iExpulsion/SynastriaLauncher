using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Mods;

public enum ModState
{
    NotInstalled,
    Installed,
    OtherVersion,
    Partial,
    Disabled,
}

public sealed record ModStatus(ModDefinition Mod, ModState State, IReadOnlyList<string> MissingRequirements);

public static class ModInstaller
{
    public const string DisabledFolder = "_Disabled";

    // System DLL names that mods ship as proxy loaders (the game loads them and they load the mod). The DLL Loader
    // patch already loads mods, so a proxy would load them twice; the launcher never installs one.
    public static readonly IReadOnlySet<string> ProxyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "version.dll", "dinput8.dll", "dsound.dll", "winmm.dll", "winhttp.dll", "wininet.dll", "dxgi.dll", "d3d8.dll",
        "ddraw.dll", "msimg32.dll", "opengl32.dll",
    };

    public static bool IsProxy(ModFile file) => ProxyNames.Contains(Path.GetFileName(file.Path));

    public static string ResolvePath(string gameFolder, string dllFolder, string path) =>
        Path.Combine(gameFolder, path.Replace("{dlls}", dllFolder).Replace('/', Path.DirectorySeparatorChar));

    // The DLL Loader doesn't search subfolders, so a DLL mod is switched off by parking its DLL folder files in
    // _Disabled. Config files elsewhere (DXVK's WTF\dxvk.conf) stay put; nothing reads them without the DLL.
    public static bool CanDisable(ModDefinition mod) =>
        mod.Files.Where(f => !f.Config).All(InDllFolder) && mod.Files.Any(InDllFolder);

    private static bool InDllFolder(ModFile file) => file.Path.StartsWith("{dlls}/", StringComparison.Ordinal);

    private static string DisabledPath(string gameFolder, string dllFolder, string path) =>
        ResolvePath(gameFolder, dllFolder, path.Replace("{dlls}/", $"{{dlls}}/{DisabledFolder}/"));

    // Where a file sits while its mod is disabled; null for files outside the DLL folder, which never move.
    public static string? ParkedPath(string gameFolder, string dllFolder, string path) =>
        path.StartsWith("{dlls}/", StringComparison.Ordinal) ? DisabledPath(gameFolder, dllFolder, path) : null;

    public static void SetEnabled(ModDefinition mod, string gameFolder, string dllFolder, bool enabled)
    {
        foreach (var file in mod.Files.Where(InDllFolder))
        {
            var live = ResolvePath(gameFolder, dllFolder, file.Path);
            var parked = DisabledPath(gameFolder, dllFolder, file.Path);
            var (from, to) = enabled ? (parked, live) : (live, parked);
            if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, overwrite: true);
            }
        }
    }

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Installed when every required file is there and matches the pinned hash, this launcher's bundled copy, or what
    // the launcher wrote for this version (bundled DLLs are compiled again for each launcher release, so an earlier
    // launcher's build of the same source differs byte for byte); OtherVersion when they're all there but differ
    // (installed by hand or by the friend pack, or an older version).
    public static ModState Detect(ModDefinition mod, string gameFolder, string dllFolder, ModRecord? record)
    {
        var files = mod.Files.Where(f => !f.Config && !f.Optional).ToList();
        var present = files.Where(f => File.Exists(ResolvePath(gameFolder, dllFolder, f.Path))).ToList();
        var matches = present.Count == files.Count && files.All(f =>
        {
            var expected = f.Sha256 ?? (f.Bundled is not null ? BundledFiles.Sha256(f.Bundled) : null);
            var recorded = record?.Version == mod.Version ? record.Hashes.GetValueOrDefault(f.Path) : null;
            var actual = Sha256Of(ResolvePath(gameFolder, dllFolder, f.Path));
            return actual == expected || actual == recorded;
        });
        var parked = CanDisable(mod) && files.All(f => File.Exists(DisabledPath(gameFolder, dllFolder, f.Path)));
        return present.Count == 0 && parked ? ModState.Disabled
            : present.Count == 0 ? ModState.NotInstalled
            : present.Count < files.Count ? ModState.Partial
            : matches ? ModState.Installed
            : ModState.OtherVersion;
    }

    // Downloads each source once, checks pinned hashes before writing anything, then writes the files. Returns the
    // record to save, with every written file's hash.
    public static async Task<ModRecord> InstallAsync(GitHubKeyless github, ModDefinition mod, string gameFolder,
        string dllFolder, CancellationToken cancel = default)
    {
        var temp = Directory.CreateTempSubdirectory("launcher-mod-").FullName;
        try
        {
            var downloads = new Dictionary<string, string>();
            var firstPresets = mod.Files.Select(f => f.Presets is [{ IsDownloaded: true } first, ..] ? first.Url : null);
            foreach (var url in mod.Files.Select(f => f.Url).Concat(firstPresets).OfType<string>().Distinct())
            {
                var file = Path.Combine(temp, $"{downloads.Count}{Path.GetExtension(new Uri(url).AbsolutePath)}");
                await github.DownloadAsync(url, file, cancel);
                downloads[url] = file;
            }
            var staged = new List<(ModFile File, byte[] Bytes)>();
            foreach (var file in mod.Files.Where(f => !ModPresets.InstallsNothing(f)))
            {
                var bytes = ReadSource(file, downloads);
                var hash = bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (IsProxy(file))
                {
                    throw new InvalidDataException($"{Path.GetFileName(file.Path)} is a proxy loader, which the launcher doesn't install");
                }
                else if (bytes is null && !file.Optional)
                {
                    throw new InvalidDataException(file.Bundled is not null
                        ? $"{Path.GetFileName(file.Path)} isn't included in this build of the launcher"
                        : $"{file.Entry ?? file.Path} wasn't found in the download");
                }
                else if (file.Sha256 is not null && hash != file.Sha256)
                {
                    throw new InvalidDataException($"{mod.Name}'s author changed {Path.GetFileName(file.Path)} since the launcher last checked it, so nothing was installed. Try again later: Check for Updates loads the launcher's updated mod list once it's out");
                }
                else if (bytes is not null)
                {
                    staged.Add((file, bytes));
                }
            }
            var hashes = new Dictionary<string, string>();
            foreach (var (file, bytes) in staged)
            {
                var target = ResolvePath(gameFolder, dllFolder, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!file.Config || !File.Exists(target))
                {
                    await File.WriteAllBytesAsync(target, bytes, cancel);
                }
                if (!file.Config)
                {
                    hashes[file.Path] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                }
            }
            return new ModRecord(mod.Version, hashes, DateTime.Now);
        }
        finally
        {
            SafePaths.DeleteTemp(temp);
        }
    }

    private static byte[]? ReadSource(ModFile file, IReadOnlyDictionary<string, string> downloads)
    {
        byte[]? bytes;
        if (file.Presets is [var first, ..])
        {
            bytes = ModPresets.Content(first) ?? (first.Url is { } url ? ReadUrl(url, first.Entry, downloads) : null);
            if (bytes is not null && first.IsDownloaded)
            {
                ModPresets.Store(first, bytes);
            }
        }
        else if (file.Bundled is not null)
        {
            bytes = BundledFiles.Read(file.Bundled);
        }
        else if (file.Content is not null)
        {
            bytes = Encoding.UTF8.GetBytes(file.Content);
        }
        else if (file.Url is not null)
        {
            bytes = ReadUrl(file.Url, file.Entry, downloads);
        }
        else
        {
            bytes = null;
        }
        return bytes;
    }

    private static byte[]? ReadUrl(string url, string? entry, IReadOnlyDictionary<string, string> downloads) =>
        entry is null ? File.ReadAllBytes(downloads[url]) : ReadEntry(downloads[url], entry);

    // An archive entry matched by file name at any depth. A damaged or unknown archive is reported as invalid data,
    // like the rest of the launcher's download problems.
    public static byte[]? ReadEntry(string archivePath, string name)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory &&
                string.Equals(Path.GetFileName(e.Key?.Replace('\\', '/') ?? ""), name, StringComparison.OrdinalIgnoreCase));
            return entry is null ? null : ReadAll(entry);
        }
        catch (Exception e) when (e is SharpCompress.Common.ExtractionException or SharpCompress.Common.ArchiveException
            or InvalidOperationException or NotSupportedException or EndOfStreamException)
        {
            throw new InvalidDataException($"{Path.GetFileName(archivePath)} couldn't be read as an archive: {e.Message}", e);
        }
    }

    private static byte[] ReadAll(IArchiveEntry entry)
    {
        using var stream = entry.OpenEntryStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // Removes the mod's files (parked ones too), leaving its config files for a later reinstall.
    public static void Uninstall(ModDefinition mod, string gameFolder, string dllFolder)
    {
        var paths = mod.Files.Where(f => !f.Config)
            .SelectMany(f => InDllFolder(f)
                ? new[] { ResolvePath(gameFolder, dllFolder, f.Path), DisabledPath(gameFolder, dllFolder, f.Path) }
                : [ResolvePath(gameFolder, dllFolder, f.Path)]);
        foreach (var path in paths.Where(File.Exists))
        {
            File.Delete(path);
        }
    }
}
