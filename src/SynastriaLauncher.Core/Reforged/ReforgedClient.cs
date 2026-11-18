using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SynastriaLauncher.Core.Reforged;

public sealed record RemoteFile(long Size, string ETag);

// Talks to Project Reforged's download bucket: HEAD for each file's size and ETag, resumable downloads, and the
// version numbers shown on the downloads page (display only).
public sealed partial class ReforgedClient : IDisposable
{
    private readonly HttpClient _http;

    public ReforgedClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SynastriaLauncher");
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose() => _http.Dispose();

    public static string Url(string bucketPath) => ReforgedCatalog.BucketBase + bucketPath;

    public async Task<RemoteFile> HeadAsync(string bucketPath, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Url(bucketPath));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag?.Tag.Trim('"') ?? throw new HttpRequestException($"No ETag for {bucketPath}");
        return new RemoteFile(response.Content.Headers.ContentLength ?? 0, etag);
    }

    [GeneratedRegex("dl-version[^>]*>.*?v(\\d+(?:\\.\\d+)+)|href=\"[^\"]*Wrath%20of%20the%20Lich%20King/([^\"]+\\.mpq)\"", RegexOptions.Singleline)]
    private static partial Regex VersionOrLink();

    // Bucket path -> the version the downloads page shows above its link; empty when the page can't be read.
    public async Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(CancellationToken cancel = default)
    {
        var versions = new Dictionary<string, string>();
        try
        {
            var html = await _http.GetStringAsync(ReforgedCatalog.DownloadsPage, cancel);
            string? version = null;
            foreach (Match match in VersionOrLink().Matches(html))
            {
                if (match.Groups[1].Success)
                {
                    version = match.Groups[1].Value;
                }
                else if (version is not null)
                {
                    versions[match.Groups[2].Value] = version;
                }
            }
        }
        catch (HttpRequestException)
        {
            versions.Clear();
        }
        return versions;
    }

    // Downloads into partPath, picking up where an earlier attempt stopped when the server still has the same file. A
    // .part that's already full size is left for the caller to verify (and delete if it doesn't match).
    public async Task DownloadAsync(string bucketPath, string partPath, RemoteFile remote, IProgress<long>? progress = null,
        CancellationToken cancel = default)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing == remote.Size && existing > 0)
        {
            progress?.Report(existing);
        }
        else
        {
            await DownloadFromAsync(bucketPath, partPath, remote, existing < remote.Size ? existing : 0, progress, cancel);
        }
    }

    private async Task DownloadFromAsync(string bucketPath, string partPath, RemoteFile remote, long start, IProgress<long>? progress,
        CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(bucketPath));
        if (start > 0)
        {
            request.Headers.Range = new RangeHeaderValue(start, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{remote.ETag}\""));
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var resumed = response.StatusCode == HttpStatusCode.PartialContent;
        var done = resumed ? start : 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        await using var output = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1);
        var buffer = new byte[1024 * 1024];
        var read = await input.ReadAsync(buffer, cancel);
        while (read > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            progress?.Report(done);
            read = await input.ReadAsync(buffer, cancel);
        }
    }
}
