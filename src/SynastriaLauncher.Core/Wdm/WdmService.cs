using SynastriaLauncher.Core.Addons;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Wdm;

public enum WdmState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    VersionUnknown,
    Linked,
    InReforged,
}

public sealed record WdmStatus(WdmPart Part, WdmState State, string? InstalledTag, string? LatestTag)
{
    public bool IsInstalled => State is not (WdmState.NotInstalled or WdmState.InReforged);
}

// Installs, updates and removes WDM's parts from their GitHub releases. The launcher records the release tag it
// installed per part (launcher.json); a copy it didn't install reads as Version Unknown until it's updated once.
public static class WdmService
{
    public static bool IsInstalled(WdmPart part, string gameFolder, string locale) =>
        part.Kind == WdmKind.Patch
            ? File.Exists(part.PatchPath(gameFolder, locale))
            : part.Folders.All(f => Directory.Exists(Path.Combine(GameFolder.AddOnsPath(gameFolder), f)));

    public static bool IsLinked(WdmPart part, string gameFolder, string locale) =>
        part.Kind == WdmKind.Patch
            ? File.Exists(part.PatchPath(gameFolder, locale)) && new FileInfo(part.PatchPath(gameFolder, locale)).LinkTarget is not null
            : part.Folders.Select(f => new DirectoryInfo(Path.Combine(GameFolder.AddOnsPath(gameFolder), f))).Any(d => d.Exists && d.LinkTarget is not null);

    public static WdmStatus Evaluate(WdmPart part, string gameFolder, string locale, string? recordedTag, string? latestTag)
    {
        var installed = IsInstalled(part, gameFolder, locale);
        var state = !installed && part.Kind == WdmKind.Patch && WdmCatalog.ReforgedMaps(gameFolder) ? WdmState.InReforged
            : !installed ? WdmState.NotInstalled
            : IsLinked(part, gameFolder, locale) ? WdmState.Linked
            : recordedTag is null ? WdmState.VersionUnknown
            : latestTag is not null && latestTag != recordedTag ? WdmState.UpdateAvailable
            : WdmState.Installed;
        return new WdmStatus(part, state, recordedTag, latestTag);
    }

    // WDM's map patches installed beside Reforged's maps, which already hold them: they'd only replace Reforged's
    // larger art with WDM's.
    public static IReadOnlyList<WdmPart> ReforgedConflicts(string gameFolder, string locale) =>
        WdmCatalog.ReforgedMaps(gameFolder)
            ? WdmCatalog.Parts.Where(p => p.Kind == WdmKind.Patch && IsInstalled(p, gameFolder, locale)).ToList()
            : [];

    // Parts this one needs that aren't installed. Reforged's maps stand in for Dungeon Maps.
    public static IReadOnlyList<WdmPart> MissingNeeds(WdmPart part, string gameFolder, string locale) =>
        part.NeededParts.Select(WdmCatalog.Get)
            .Where(n => !IsInstalled(n, gameFolder, locale) && !(n.Id == WdmCatalog.Map && WdmCatalog.ReforgedMaps(gameFolder)))
            .ToList();

    // The newest release that carries the part's files: the latest release, or for a pre-release part the newest
    // tag in the releases feed with its asset.
    public static async Task<string?> LatestTagAsync(GitHubKeyless http, WdmPart part, string locale, CancellationToken cancel = default)
    {
        string? tag = null;
        if (part.PreRelease)
        {
            var tags = WdmCatalog.ParseReleaseTags(await http.GetTextOrNullAsync($"https://github.com/{part.Repo}/releases.atom", cancel) ?? "");
            foreach (var candidate in tags.Take(6))
            {
                if (await AssetUrlAsync(http, part, candidate, part.Assets[0], locale, cancel) is not null)
                {
                    tag = candidate;
                    break;
                }
            }
        }
        else
        {
            tag = await http.GetLatestReleaseTagAsync(part.Repo, cancel);
        }
        return tag;
    }

    private static Task<string?> AssetUrlAsync(GitHubKeyless http, WdmPart part, string tag, string asset, string locale, CancellationToken cancel)
    {
        var name = part.AssetFor(asset, locale);
        return http.GetReleaseAssetUrlAsync(part.Repo, tag, n => n.Equals(name, StringComparison.OrdinalIgnoreCase), cancel);
    }

    // Downloads every file first, then writes them: a patch into Data\<locale>, addons through the addon installer.
    public static async Task InstallAsync(GitHubKeyless http, WdmPart part, string tag, string gameFolder, string locale,
        bool keepBackup, CancellationToken cancel = default)
    {
        var temp = Directory.CreateTempSubdirectory("launcher-wdm-").FullName;
        try
        {
            var files = new List<string>();
            foreach (var asset in part.Assets)
            {
                var url = await AssetUrlAsync(http, part, tag, asset, locale, cancel)
                    ?? throw new InvalidDataException($"{part.AssetFor(asset, locale)} isn't in WDM's {tag} release");
                var file = Path.Combine(temp, part.AssetFor(asset, locale));
                await http.DownloadAsync(url, file, cancel);
                files.Add(file);
            }
            Write(part, files, gameFolder, locale, keepBackup);
        }
        finally
        {
            SafePaths.DeleteTemp(temp);
        }
    }

    public static void Write(WdmPart part, IReadOnlyList<string> files, string gameFolder, string locale, bool keepBackup)
    {
        if (IsLinked(part, gameFolder, locale))
        {
            throw new LinkedFolderException(part.Name);
        }
        else if (part.Kind == WdmKind.Patch)
        {
            var target = part.PatchPath(gameFolder, locale);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(files[0], target + ".part", overwrite: true);
            File.Move(target + ".part", target, overwrite: true);
        }
        else
        {
            foreach (var zip in files)
            {
                AddonInstaller.InstallFromZip(zip, GameFolder.AddOnsPath(gameFolder), keepBackup);
            }
        }
    }

    public static void Uninstall(WdmPart part, string gameFolder, string locale)
    {
        if (part.Kind == WdmKind.Patch)
        {
            File.Delete(part.PatchPath(gameFolder, locale));
        }
        else
        {
            AddonInstaller.Uninstall(GameFolder.AddOnsPath(gameFolder), part.Folders);
        }
    }
}
