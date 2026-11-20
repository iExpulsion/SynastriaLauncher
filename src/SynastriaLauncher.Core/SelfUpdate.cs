using System.Reflection;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core;

public sealed record LauncherUpdate(string Tag, Version Version, string DownloadUrl);

// The launcher updates itself from its own latest GitHub release, keyless like the addon checks.
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

    // A single-file exe reads assemblies out of its own file the first time they're used, so a running launcher
    // must never be renamed or replaced: 0.1.0 and 1.0.0 swapped themselves first and then failed to load
    // System.Diagnostics.Process to restart. The old launcher only downloads the new exe beside itself and starts
    // it with FinishArg; the new one waits for the old to exit, copies itself into place and starts that copy.
    public const string FinishArg = "--finish-update";

    public static string IncomingPath(string exePath) => exePath + ".new";

    public static async Task<string> DownloadAsync(GitHubKeyless github, LauncherUpdate update, string exePath,
        CancellationToken cancel = default)
    {
        var incoming = IncomingPath(exePath);
        try
        {
            await github.DownloadAsync(update.DownloadUrl, incoming, cancel);
            if (!IsExe(incoming))
            {
                throw new InvalidDataException("The downloaded update isn't a program file");
            }
            return incoming;
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }
    }

    private static bool IsExe(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.Length > 1024 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }

    public static IReadOnlyList<string> FinishArgs(string exePath, int waitForProcessId) =>
        [FinishArg, exePath, waitForProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    // Run by the new exe: waits for the old launcher to exit, then replaces it through a temporary copy so a failed
    // copy never leaves a broken exe. Retries while the old file is still locked (exiting, or being scanned).
    public static void Finish(string newExe, string exePath, int waitForProcessId, TimeSpan? lockWait = null)
    {
        WaitForExit(waitForProcessId);
        var temp = exePath + ".tmp";
        var deadline = DateTime.UtcNow + (lockWait ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                File.Copy(newExe, temp, overwrite: true);
                File.Move(temp, exePath, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    // Removes what an update leaves beside the exe: the downloaded copy (still running for a moment after it
    // starts the replaced exe), a failed temporary copy, and older launchers' renamed exe.
    public static void CleanUp(string exePath)
    {
        foreach (var leftover in new[] { IncomingPath(exePath), exePath + ".tmp", exePath + ".old" })
        {
            TryDelete(leftover);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held by the process that just exited; the next start removes it.
        }
    }
}
