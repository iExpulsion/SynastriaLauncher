namespace SynastriaLauncher.Core.Addons;

public enum AddonState
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    Unknown,
    Linked,
    RepoNotFound,
    Failed,
}

// What's in the AddOns folder for one source. Linked means a folder is a junction or symlink (a working copy, or a
// folder shared with another game install), which the launcher never replaces.
public sealed record LocalAddon(bool Installed, bool Linked, string? TocVersion, DateTime? NewestFile);

public sealed record AddonCheck(
    AddonSource Source,
    AddonState State,
    string? ReleaseTag = null,
    string? HeadSha = null,
    string? DefaultBranch = null,
    string? LocalVersion = null,
    string? Error = null,
    IReadOnlyList<string>? ChangedFiles = null,
    bool LatestCommits = false)
{
    public bool HasLocalEdits => ChangedFiles is { Count: > 0 };

    public bool CanInstall => State is AddonState.NotInstalled or AddonState.UpdateAvailable or AddonState.Unknown;

    // What an install would put in: the release, or the head commit for a repo without releases or on Latest Commits.
    public bool FollowsRelease => ReleaseTag is not null && !LatestCommits;

    public string LatestLabel => FollowsRelease ? ReleaseTag! : HeadSha is null ? "" : HeadSha[..7];
}

public static partial class AddonStatus
{
    public static string VersionKey(string text) => text.Trim().TrimStart('v', 'V').Trim();

    public static bool IsCurrent(string local, string remote)
    {
        var l = VersionKey(local);
        var r = VersionKey(remote);
        return Version.TryParse(l, out var lv) && Version.TryParse(r, out var rv) ? lv >= rv : l == r;
    }

    // What the install record says on its own. Following releases, only a recorded release tag counts, so commits
    // after a release never show as an update. Following commits, a recorded commit is compared with the head, and a
    // copy installed from a release is behind it. Null when the record can't tell (none, or one from the other mode).
    public static bool? RecordSays(InstallRecord? recorded, string? releaseTag, string? headSha, bool latestCommits) =>
        releaseTag is not null && !latestCommits ? recorded?.Tag is { } tag ? IsCurrent(tag, releaseTag) : null
        : recorded is { Sha: not null } && headSha is not null ? recorded.Sha == headSha
        : recorded is { Tag: not null } && headSha is not null ? false
        : null;

    // Decides a source's state: by the install record, else a differing .toc version (from the release when
    // following releases), else a matching release .toc, else whether local files are older than the last commit.
    public static AddonState Evaluate(LocalAddon local, InstallRecord? recorded, string? releaseTag, string? headSha,
        string? remoteTocVersion, DateTimeOffset? lastCommit, bool latestCommits = false)
    {
        var tocDiffers = local.TocVersion is not null && remoteTocVersion is not null &&
            VersionKey(local.TocVersion) != VersionKey(remoteTocVersion);
        var followsRelease = releaseTag is not null && !latestCommits;
        bool? current =
            RecordSays(recorded, releaseTag, headSha, latestCommits) is { } said ? said
            : tocDiffers ? IsCurrent(local.TocVersion!, remoteTocVersion!)
            : followsRelease && local.TocVersion is not null && remoteTocVersion is not null ? true
            : lastCommit is not null && local.NewestFile is not null ? local.NewestFile.Value.Date >= lastCommit.Value.LocalDateTime.Date
            : null;
        return !local.Installed ? AddonState.NotInstalled
            : local.Linked ? AddonState.Linked
            : current is null ? AddonState.Unknown
            : current.Value ? AddonState.UpToDate
            : AddonState.UpdateAvailable;
    }

    public static LocalAddon ReadLocal(string addOnsPath, IReadOnlyList<string> folders)
    {
        var paths = folders.Select(f => Path.Combine(addOnsPath, f)).ToList();
        var installed = paths.Count > 0 && folders.All(f => SafePaths.IsChildFolder(addOnsPath, f)) && paths.All(Directory.Exists);
        var linked = installed && paths.Any(p => new DirectoryInfo(p).LinkTarget is not null);
        string? toc = null;
        DateTime? newest = null;
        if (installed)
        {
            toc = paths.Select(ReadTocVersion).FirstOrDefault(v => v is not null);
            newest = paths
                .Select(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTime).DefaultIfEmpty().Max())
                .Min();
        }
        return new LocalAddon(installed, linked, toc, newest);
    }

    public static string? ReadTocVersion(string folder)
    {
        var toc = Path.Combine(folder, Path.GetFileName(folder) + ".toc");
        return File.Exists(toc) ? TocVersion(File.ReadLines(toc).Take(40)) : null;
    }

    public static string? TocVersion(IEnumerable<string> lines) =>
        lines.Select(l => TocVersionLine().Match(l))
            .Where(m => m.Success && m.Groups[1].Value.Trim().Length > 0)
            .Select(m => m.Groups[1].Value.Trim())
            .FirstOrDefault();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*##\s*Version\s*:(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TocVersionLine();

    // The .toc's Title without the game's color codes (|cAARRGGBB ... |r); null when there's none.
    public static string? TocTitle(IEnumerable<string> lines) =>
        lines.Select(l => TocTitleLine().Match(l))
            .Where(m => m.Success)
            .Select(m => ColorCode().Replace(m.Groups[1].Value, "").Trim())
            .FirstOrDefault(t => t.Length > 0);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*##\s*Title\s*:(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TocTitleLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"\|c[0-9A-Fa-f]{8}|\|r")]
    private static partial System.Text.RegularExpressions.Regex ColorCode();
}
