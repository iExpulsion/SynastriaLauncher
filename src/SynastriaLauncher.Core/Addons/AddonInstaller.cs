using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Addons;

public sealed class LinkedFolderException(string folder)
    : IOException($"{folder} is linked to a folder somewhere else, so the launcher leaves it alone");

public static class AddonInstaller
{
    // A repo with releases installs its latest one: the packaged download when there is one (it often bundles
    // libraries the source lacks), else the release's source. A repo without releases, or one the player switched to
    // Latest Commits, installs the default branch's head.
    public static async Task<InstallRecord> InstallAsync(GitHubKeyless github, AddonCheck check, string addOnsPath,
        bool keepBackup, CancellationToken cancel = default)
    {
        var repo = check.Source.Repo;
        var tag = check.LatestCommits ? null : check.ReleaseTag;
        var asset = tag is null ? null : Archives.Best(await github.GetReleaseAssetsAsync(repo, tag, cancel), a => a.Name);
        var url = asset?.Url ?? (tag is not null ? GitHubKeyless.TagSourceZipUrl(repo, tag)
            : GitHubKeyless.SourceZipUrl(repo, check.HeadSha ?? check.DefaultBranch ?? "HEAD"));
        var temp = Directory.CreateTempSubdirectory("launcher-addon-").FullName;
        try
        {
            var download = Path.Combine(temp, "download");
            await github.DownloadAsync(url, download, cancel);
            var folders = InstallFromArchive(download, addOnsPath, keepBackup);
            return new InstallRecord(tag, tag is null ? check.HeadSha : null, folders, DateTime.Now,
                InstalledFiles.Stamp(addOnsPath, folders));
        }
        finally
        {
            SafePaths.DeleteTemp(temp);
        }
    }

    // Every folder holding a .toc (outside Libs) is an addon, named after its folder, or after its shortest .toc
    // when it sits at the archive root. Only the shallowest such folders count (siblings like Foo and Foo_Options
    // both do; Foo/legacy/Old.toc doesn't). Returns the installed folder names.
    public static IReadOnlyList<string> InstallFromArchive(string archivePath, string addOnsPath, bool keepBackup)
    {
        var temp = Directory.CreateTempSubdirectory("launcher-extract-").FullName;
        try
        {
            Archives.ExtractTo(archivePath, temp);
            var top = Directory.GetFileSystemEntries(temp);
            var root = top.Length == 1 && Directory.Exists(top[0]) ? top[0] : temp;
            var groups = Directory.EnumerateFiles(temp, "*.toc", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(temp, f).Split(Path.DirectorySeparatorChar)
                    .Contains("Libs", StringComparer.OrdinalIgnoreCase))
                .GroupBy(f => Path.GetDirectoryName(f)!)
                .ToList();
            var minDepth = groups.Count == 0 ? 0 : groups.Min(g => Depth(g.Key));
            var addons = groups.Where(g => Depth(g.Key) == minDepth)
                .Select(g => (Source: g.Key, Name: g.Key == root
                    ? g.Select(Path.GetFileNameWithoutExtension).OrderBy(n => n!.Length).First()!
                    : Path.GetFileName(g.Key)))
                .ToList();
            var unusable = addons.Select(a => a.Name).FirstOrDefault(n => !SafePaths.IsChildFolder(addOnsPath, n));
            var linked = addons.Select(a => a.Name)
                .FirstOrDefault(n => SafePaths.IsChildFolder(addOnsPath, n) && IsLink(Path.Combine(addOnsPath, n)));
            if (addons.Count == 0)
            {
                throw new InvalidDataException("The download has no .toc file outside Libs, so it isn't an addon");
            }
            else if (unusable is not null)
            {
                throw new InvalidDataException($"The download names an addon folder \"{unusable}\" that can't be used, so nothing was installed");
            }
            else if (linked is not null)
            {
                throw new LinkedFolderException(linked);
            }
            else
            {
                Directory.CreateDirectory(addOnsPath);
                foreach (var (source, name) in addons)
                {
                    Replace(source, Path.Combine(addOnsPath, name), keepBackup);
                }
            }
            return addons.Select(a => a.Name).ToList();
        }
        finally
        {
            SafePaths.DeleteTemp(temp);
        }
    }

    private static int Depth(string path) => path.Split(Path.DirectorySeparatorChar).Length;

    private static bool IsLink(string folder) => Directory.Exists(folder) && new DirectoryInfo(folder).LinkTarget is not null;

    private static void Replace(string source, string target, bool keepBackup)
    {
        var backup = target + ".bak";
        if (Directory.Exists(target) && keepBackup)
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            Directory.Move(target, backup);
        }
        else if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        CopyDirectory(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    public static void Uninstall(string addOnsPath, IEnumerable<string> folders)
    {
        var names = folders.ToList();
        var unusable = names.FirstOrDefault(f => !SafePaths.IsChildFolder(addOnsPath, f));
        var paths = names.Select(f => Path.Combine(addOnsPath, f)).Where(Directory.Exists).ToList();
        var linked = paths.FirstOrDefault(IsLink);
        if (unusable is not null)
        {
            throw new IOException($"\"{unusable}\" isn't a folder inside AddOns, so nothing was removed");
        }
        else if (linked is not null)
        {
            throw new LinkedFolderException(Path.GetFileName(linked));
        }
        else
        {
            paths.ForEach(p => Directory.Delete(p, recursive: true));
        }
    }
}
