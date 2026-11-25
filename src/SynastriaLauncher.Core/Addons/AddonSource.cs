namespace SynastriaLauncher.Core.Addons;

// A GitHub repo the launcher tracks. Folders are the addon folders it installs into AddOns; TocPath is the main
// .toc inside the repo, used to compare versions of a copy installed some other way. Requires lists the repos
// installed along with it; a Library is only there for the addons that require it. Zip is a packaged addon kept as a
// file inside the repo (a collection of addons): it installs that file, and follows the commits that change it.
public sealed record AddonSource(string Repo, string Name, IReadOnlyList<string> Folders, string? TocPath, bool Curated,
    string? Description = null, IReadOnlyList<string>? Requires = null, bool Library = false, bool Wotlk = false,
    string? Zip = null)
{
    public string Author => Repo.Split('/')[0];

    public string PageUrl => Zip is null ? $"https://github.com/{Repo}" : $"https://github.com/{Repo}/blob/HEAD/{Zip}";
}

// Files holds each installed file's size and write time (paths relative to AddOns), so later edits can be spotted.
// Name and Zip keep the addon's name and packaged file for when it leaves the curated list.
public sealed record InstallRecord(string? Tag, string? Sha, IReadOnlyList<string>? Folders, DateTime InstalledAt,
    Dictionary<string, FileStamp>? Files = null, string? Name = null, string? Zip = null);

public sealed record FileStamp(long Size, DateTime WrittenUtc);

public static class InstalledFiles
{
    public static Dictionary<string, FileStamp> Stamp(string addOnsPath, IEnumerable<string> folders) =>
        folders.Select(f => Path.Combine(addOnsPath, f))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .ToDictionary(f => Path.GetRelativePath(addOnsPath, f), f => new FileStamp(new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));

    // Files changed, added or removed since install. The game's own cache files (WTF) live elsewhere, so anything
    // different inside the addon's folders was done by hand or by another tool.
    public static IReadOnlyList<string> Changed(string addOnsPath, InstallRecord? record)
    {
        var now = record?.Files is { Count: > 0 } && record.Folders is { } folders ? Stamp(addOnsPath, folders) : null;
        return now is null ? []
            : now.Where(p => !record!.Files!.TryGetValue(p.Key, out var then) || then != p.Value).Select(p => p.Key)
                .Concat(record!.Files!.Keys.Where(k => !now.ContainsKey(k)))
                .ToList();
    }
}

