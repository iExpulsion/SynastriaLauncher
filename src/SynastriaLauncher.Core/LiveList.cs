using System.Text.Json;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core;

// A list that ships inside the launcher and is also published in the launcher's repo, so it can change without a
// release: mods.json and addons.json.
public interface ILiveList
{
    int Revision { get; }

    // The oldest launcher that understands this list; older ones keep their built-in copy.
    string? MinLauncher { get; }

    // Problems that make the list unsafe or unusable; a published list with any is ignored.
    IReadOnlyList<string> Problems();
}

public static class LiveList
{
    public const string RepoFolder = "https://raw.githubusercontent.com/iExpulsion/SynastriaLauncher/main/src/SynastriaLauncher.Core/";

    public static bool Supports(ILiveList list, Version launcher) =>
        list.MinLauncher is null || Version.TryParse(list.MinLauncher, out var min) && launcher >= min;

    // The published list when it's newer, valid and meant for this launcher; otherwise the built-in one.
    public static T Pick<T>(T embedded, T? published, Version launcher) where T : class, ILiveList =>
        published is not null && published.Revision > embedded.Revision && Supports(published, launcher)
            && IsValid(published) ? published : embedded;

    // JSON doesn't enforce required fields, so a list missing one can fail its own checks; that makes it invalid.
    private static bool IsValid(ILiveList list)
    {
        try
        {
            return list.Problems().Count == 0;
        }
        catch (Exception e) when (e is NullReferenceException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // A file beside launcher.json replaces both (for testing); otherwise the newer of the built-in and published lists.
    public static async Task<T> LoadAsync<T>(GitHubKeyless github, string url, string overridePath, Func<string, T> parse,
        T embedded, Version launcher, CancellationToken cancel = default) where T : class, ILiveList
    {
        T result;
        if (File.Exists(overridePath))
        {
            result = parse(await File.ReadAllTextAsync(overridePath, cancel));
        }
        else
        {
            T? published;
            try
            {
                published = await github.GetTextOrNullAsync(url, cancel) is { } json ? parse(json) : null;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                published = null;
            }
            result = Pick(embedded, published, launcher);
        }
        return result;
    }
}
