using System.Reflection;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core;

public sealed record LauncherUpdate(string Tag, Version Version, string DownloadUrl);

// The launcher updates itself from its own latest GitHub release, keyless like the addon checks. A running exe
// can be renamed but not overwritten, so the new one moves into place and the old one is deleted next start.
public static class SelfUpdate
{
    public const string Repo = "iExpulsion/SynastriaLauncher";
    public const string AssetName = "SynastriaLauncher.exe";

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static Version? ParseTag(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var version) ? version : null;

    public static async Task<LauncherUpdate?> CheckAsync(GitHubKeyless github, Version current, CancellationToken cancel = default)
    {
        var tag = await github.GetLatestReleaseTagAsync(Repo, cancel);
        var version = tag is null ? null : ParseTag(tag);
        var url = version is not null && Normalize(version) > Normalize(current)
            ? await github.GetReleaseAssetUrlAsync(Repo, tag!, n => n.Equals(AssetName, StringComparison.OrdinalIgnoreCase), cancel)
            : null;
        return url is null ? null : new LauncherUpdate(tag!, version!, url);
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    public static async Task ApplyAsync(GitHubKeyless github, LauncherUpdate update, string exePath, CancellationToken cancel = default)
    {
        var incoming = exePath + ".new";
        var old = exePath + ".old";
        try
        {
            await github.DownloadAsync(update.DownloadUrl, incoming, cancel);
            File.Move(exePath, old, overwrite: true);
            try
            {
                File.Move(incoming, exePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                File.Move(old, exePath);
                throw;
            }
        }
        finally
        {
            File.Delete(incoming);
        }
    }

    public static void CleanUp(string exePath)
    {
        var old = exePath + ".old";
        if (File.Exists(old))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still held by the previous process for a moment; the next start removes it.
            }
        }
    }
}
