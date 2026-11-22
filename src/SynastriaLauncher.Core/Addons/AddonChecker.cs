using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Addons;

public static class AddonChecker
{
    // About two requests per repo (refs and the latest-release redirect), plus a raw .toc or commit feed only for a
    // copy the launcher didn't install. None of them count against the API limit.
    public static async Task<AddonCheck> CheckAsync(GitHubKeyless github, AddonSource source, string addOnsPath,
        InstallRecord? recorded, bool latestCommits = false, CancellationToken cancel = default)
    {
        AddonCheck check;
        try
        {
            var refs = await github.GetRefsAsync(source.Repo, cancel);
            var folders = recorded?.Folders is { Count: > 0 } known ? known : source.Folders;
            var local = AddonStatus.ReadLocal(addOnsPath, folders);
            if (refs is null)
            {
                check = new AddonCheck(source, AddonState.RepoNotFound, LocalVersion: local.TocVersion);
            }
            else
            {
                var tag = await github.GetLatestReleaseTagAsync(source.Repo, cancel);
                var followsRelease = tag is not null && !latestCommits;
                var needsFallback = local.Installed && !local.Linked
                    && AddonStatus.RecordSays(recorded, tag, refs.HeadSha, latestCommits) is null;
                var remoteToc = needsFallback && source.TocPath is not null
                    ? AddonStatus.TocVersion((await github.GetRawFileAsync(source.Repo,
                        (followsRelease ? tag : null) ?? refs.DefaultBranch ?? "HEAD", source.TocPath, cancel) ?? "").Split('\n'))
                    : null;
                var tocDiffers = local.TocVersion is not null && remoteToc is not null &&
                    AddonStatus.VersionKey(local.TocVersion) != AddonStatus.VersionKey(remoteToc);
                var tocMatchesRelease = followsRelease && local.TocVersion is not null && remoteToc is not null;
                var lastCommit = needsFallback && !tocDiffers && !tocMatchesRelease && refs.DefaultBranch is not null
                    ? await github.GetLastCommitDateAsync(source.Repo, refs.DefaultBranch, cancel)
                    : null;
                var state = AddonStatus.Evaluate(local, recorded, tag, refs.HeadSha, remoteToc, lastCommit, latestCommits);
                check = new AddonCheck(source, state, tag, refs.HeadSha, refs.DefaultBranch, local.TocVersion,
                    ChangedFiles: local.Installed && !local.Linked ? InstalledFiles.Changed(addOnsPath, recorded) : null,
                    LatestCommits: latestCommits);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
            or FormatException or ArgumentException)
        {
            check = new AddonCheck(source, AddonState.Failed, Error: e.Message);
        }
        return check;
    }

    public static async Task<IReadOnlyList<AddonCheck>> CheckAllAsync(GitHubKeyless github, IEnumerable<AddonSource> sources,
        string addOnsPath, IReadOnlyDictionary<string, InstallRecord> records, Func<string, bool> latestCommits,
        CancellationToken cancel = default)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                return await CheckAsync(github, source, addOnsPath, records.GetValueOrDefault(source.Repo),
                    latestCommits(source.Repo), cancel);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }
}
