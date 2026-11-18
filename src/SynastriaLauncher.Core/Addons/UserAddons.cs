using System.Text.RegularExpressions;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Addons;

public static partial class UserAddons
{
    [GeneratedRegex(@"^(?:https?://github\.com/)?([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepoPattern();

    // Accepts "owner/repo" or a GitHub URL; null when it's neither.
    public static string? ParseRepo(string text)
    {
        var match = RepoPattern().Match(text.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    // Folder names from a repo's .toc paths: the shallowest .toc folders outside Libs, named after the folder, or
    // after the shortest .toc at the repo root. The first folder's .toc is the one versions are read from.
    public static (IReadOnlyList<string> Folders, string? TocPath) FoldersFromTocPaths(IEnumerable<string> tocPaths)
    {
        var groups = tocPaths
            .Where(p => !p.Split('/').Contains("Libs", StringComparer.OrdinalIgnoreCase))
            .GroupBy(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : "")
            .ToList();
        var depth = (string dir) => dir.Length == 0 ? 0 : dir.Split('/').Length;
        var min = groups.Count == 0 ? 0 : groups.Min(g => depth(g.Key));
        var folders = groups.Where(g => depth(g.Key) == min)
            .Select(g =>
            {
                var name = g.Key.Length == 0
                    ? g.Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(n => n.Length).First()
                    : g.Key.Split('/')[^1];
                return (Name: name, Toc: g.Key.Length == 0 ? $"{name}.toc" : $"{g.Key}/{name}.toc");
            })
            .Where(f => SafePaths.IsFolderName(f.Name))
            .ToList();
        return (folders.Select(f => f.Name).ToList(), folders.FirstOrDefault().Toc);
    }

    // Looks the repo up (two API requests: its file list and About line; its .toc, for the name, comes from raw
    // files). Null when GitHub has no such public repo.
    public static async Task<AddonSource?> CreateAsync(GitHubKeyless github, string repo, CancellationToken cancel = default)
    {
        var refs = await github.GetRefsAsync(repo, cancel);
        AddonSource? source = null;
        if (refs?.DefaultBranch is { } branch)
        {
            var (folders, toc) = FoldersFromTocPaths(await github.GetTocPathsAsync(repo, branch, cancel));
            var tocText = toc is null ? null : await github.GetRawFileAsync(repo, branch, toc, cancel);
            var name = AddonStatus.TocTitle((tocText ?? "").Split('\n')) ?? folders.FirstOrDefault() ?? repo.Split('/')[1];
            source = new AddonSource(repo, name, folders, toc, false, await github.GetDescriptionAsync(repo, cancel));
        }
        return source;
    }
}
