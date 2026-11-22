using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SynastriaLauncher.Core.GitHub;

public sealed record ReleaseZip(string Url, bool FromRelease);

public sealed record ReleaseAsset(string Name, string Url);

// Reads GitHub without the REST API (and its 60-requests-an-hour limit for clients without a token): Git's smart
// HTTP ref listing, the releases/latest redirect, the release assets fragment, raw files and Atom feeds. The API
// is used only as a fallback for one release's assets and when a repo is added by hand.
public sealed partial class GitHubKeyless : IDisposable
{
    private const string UserAgent = "SynastriaLauncher";
    private readonly HttpClient _http;
    private readonly HttpClient _noRedirect;

    public GitHubKeyless(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _noRedirect = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        foreach (var client in new[] { _http, _noRedirect })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(60);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _noRedirect.Dispose();
    }

    // Null when the repo doesn't exist or is private (GitHub answers 401 or 404 then).
    public async Task<GitRefs?> GetRefsAsync(string repo, CancellationToken cancel = default)
    {
        using var response = await _http.GetAsync($"https://github.com/{repo}.git/info/refs?service=git-upload-pack", cancel);
        GitRefs? refs = null;
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized))
        {
            response.EnsureSuccessStatusCode();
            refs = GitRefs.Parse(await response.Content.ReadAsByteArrayAsync(cancel));
        }
        return refs;
    }

    // The latest release's tag, from where releases/latest redirects; null when the repo has no releases.
    public async Task<string?> GetLatestReleaseTagAsync(string repo, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{repo}/releases/latest");
        using var response = await _noRedirect.SendAsync(request, cancel);
        var location = response.Headers.Location?.ToString() ?? "";
        var marker = location.IndexOf("/releases/tag/", StringComparison.Ordinal);
        return marker >= 0 ? Uri.UnescapeDataString(location[(marker + "/releases/tag/".Length)..]) : null;
    }

    [GeneratedRegex("href=\"(/[^\"]+/releases/download/[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AssetLink();

    // The release's first asset whose file name matches; null when none does.
    public async Task<string?> GetReleaseAssetUrlAsync(string repo, string tag, Func<string, bool> matches,
        CancellationToken cancel = default) =>
        (await GetReleaseAssetsAsync(repo, tag, cancel)).FirstOrDefault(a => matches(a.Name))?.Url;

    // The release's downloads (not GitHub's own source archives), in the order the release lists them. The assets
    // fragment is undocumented, so when it doesn't look like a release page at all, the API is asked instead.
    public async Task<IReadOnlyList<ReleaseAsset>> GetReleaseAssetsAsync(string repo, string tag, CancellationToken cancel = default)
    {
        var html = await GetTextOrNullAsync(
            $"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", cancel) ?? "";
        var links = AssetLink().Matches(html)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .Select(href => new ReleaseAsset(Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]), "https://github.com" + href))
            .ToList();
        return links.Count > 0 || html.Contains("Source code", StringComparison.Ordinal) ? links
            : await GetReleaseAssetsFromApiAsync(repo, tag, cancel);
    }

    internal sealed record ApiAsset([property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url);

    internal sealed record ApiRelease([property: JsonPropertyName("assets")] List<ApiAsset> Assets);

    private async Task<IReadOnlyList<ReleaseAsset>> GetReleaseAssetsFromApiAsync(string repo, string tag, CancellationToken cancel)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", cancel);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync(CoreJson.Default.ApiRelease, cancel);
        return release?.Assets.Select(a => new ReleaseAsset(a.Name, a.Url)).ToList() ?? [];
    }

    public static string SourceZipUrl(string repo, string sha) => $"https://codeload.github.com/{repo}/zip/{sha}";

    // GitHub's source archive of a tag, which every release has.
    public static string TagSourceZipUrl(string repo, string tag) =>
        $"https://codeload.github.com/{repo}/zip/refs/tags/{Uri.EscapeDataString(tag)}";

    public Task<string?> GetRawFileAsync(string repo, string gitRef, string path, CancellationToken cancel = default)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return GetTextOrNullAsync($"https://raw.githubusercontent.com/{repo}/{Uri.EscapeDataString(gitRef)}/{escaped}", cancel);
    }

    [GeneratedRegex(@"<entry>[\s\S]*?<updated>([^<]+)</updated>")]
    private static partial Regex FirstEntryUpdated();

    public async Task<DateTimeOffset?> GetLastCommitDateAsync(string repo, string branch, CancellationToken cancel = default)
    {
        var atom = await GetTextOrNullAsync($"https://github.com/{repo}/commits/{Uri.EscapeDataString(branch)}.atom", cancel) ?? "";
        var match = FirstEntryUpdated().Match(atom);
        return match.Success && DateTimeOffset.TryParse(match.Groups[1].Value, out var date) ? date : null;
    }

    internal sealed record ApiTreeEntry([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("type")] string Type);

    internal sealed record ApiTree([property: JsonPropertyName("tree")] List<ApiTreeEntry> Tree);

    internal sealed record ApiRepo([property: JsonPropertyName("description")] string? Description);

    // The repo's About line; uses the API, so it's only for a repo the user adds by hand.
    public async Task<string?> GetDescriptionAsync(string repo, CancellationToken cancel = default)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{repo}", cancel);
        var about = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(CoreJson.Default.ApiRepo, cancel)
            : null;
        return string.IsNullOrWhiteSpace(about?.Description) ? null : about.Description.Trim();
    }

    // Uses the API (one request), so it's only for a repo the user adds by hand.
    public async Task<IReadOnlyList<string>> GetTocPathsAsync(string repo, string branch, CancellationToken cancel = default)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{repo}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1", cancel);
        response.EnsureSuccessStatusCode();
        var tree = await response.Content.ReadFromJsonAsync(CoreJson.Default.ApiTree, cancel);
        return tree?.Tree.Where(e => e.Type == "blob" && e.Path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path).ToList() ?? [];
    }

    // Downloads https URLs, or copies file:// URLs (for testing local packages).
    public async Task DownloadAsync(string url, string destination, CancellationToken cancel = default)
    {
        var uri = new Uri(url);
        if (uri.IsFile)
        {
            File.Copy(uri.LocalPath, destination, overwrite: true);
        }
        else
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(destination);
            await response.Content.CopyToAsync(file, cancel);
        }
    }

    public async Task<string?> GetTextOrNullAsync(string url, CancellationToken cancel = default)
    {
        using var response = await _http.GetAsync(url, cancel);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancel) : null;
    }
}
