using System.Text.Json;
using System.Text.Json.Serialization;

namespace SynastriaLauncher.Core.Reforged;

public enum ReforgedPartState
{
    NotInstalled,
    Current,
    UpdateAvailable,
    Partial,
}

// InstalledVariant is the variant whose files are all there (current or not), or the one last installed. Targets
// maps each of the part's linked files to what the link points to. Parked parts were switched off: their files (or
// links) wait in Data\_Disabled, where the game doesn't load them, and State describes those files.
public sealed record PartStatus(ReforgedPart Part, ReforgedPartState State, string? InstalledVariant, bool Linked,
    IReadOnlyDictionary<string, bool> CurrentFiles, IReadOnlyDictionary<string, string>? Targets = null, bool Parked = false)
{
    public bool IsOn => State != ReforgedPartState.NotInstalled && !Parked;
}

// SharedFolder is the one folder every linked part points into, when there is one.
public sealed record ReforgedStatus(
    IReadOnlyList<PartStatus> Parts,
    IReadOnlyDictionary<string, RemoteFile> Remote,
    IReadOnlyDictionary<string, string> Versions,
    string? SharedFolder = null)
{
    public PartStatus Get(string id) => Parts.First(p => p.Part.Id == id);

    public long Size(ReforgedVariant variant) => variant.BucketPaths.Sum(p => Remote[p].Size);

    public bool AnyInstalled => Parts.Any(p => p.State != ReforgedPartState.NotInstalled);

    // Selection matching what's on now: each part's installed variant, null when it's missing or switched off.
    public Dictionary<string, string?> CurrentSelection() =>
        Parts.ToDictionary(p => p.Part.Id, p => p.IsOn ? p.InstalledVariant : null);
}

// Target is where the downloaded file goes: Data, or a file in the shared folder (the link in Data stays, or with
// CreateLink is made once the file is there).
public sealed record ReforgedDownload(string File, string BucketPath, RemoteFile Remote, string? Target = null, bool CreateLink = false)
{
    public bool ThroughLink => Target is not null;
}

// A link to make in Data for a file the shared folder already has.
public sealed record ReforgedLink(string File, string Target);

// Parks move files (or links) from Data into Data\_Disabled; Unparks move them back. Nothing is ever deleted.
public sealed record ReforgedPlan(IReadOnlyList<ReforgedDownload> Downloads, IReadOnlyList<string> Parks, IReadOnlyList<string> Unparks,
    IReadOnlyList<ReforgedLink> Links)
{
    public long DownloadBytes => Downloads.Sum(d => d.Remote.Size);
    public bool IsEmpty => Downloads.Count == 0 && Parks.Count == 0 && Unparks.Count == 0 && Links.Count == 0;
}

public sealed record ReforgedProgress(string File, string Phase, long Done, long Total, int Index, int Count);

// Installs Project Reforged's patches into the game's Data folder from the official bucket, checks them against the
// bucket's ETags, and removes only Reforged's own files. A file in Data that links to a shared folder is updated
// where it points (the link stays, so every client sharing it gets the update); turning such a part off removes
// only the link, and its variant can't be switched (that would change the shared file for everyone). Hash results
// are cached by path, size and write time, since hashing ~20 GB takes a while.
public sealed class ReforgedService(ReforgedClient client, string cachePath)
{
    public static string DefaultCachePath => Path.Combine(LauncherData.DefaultFolder, "reforged.json");

    public static string DataFolder(string gameFolder) => Path.Combine(gameFolder, "Data");

    public const string DisabledFolderName = "_Disabled";

    // The client loads only the archives directly in Data (and its locale folder), so files here are off.
    public static string DisabledFolder(string gameFolder) => Path.Combine(DataFolder(gameFolder), DisabledFolderName);

    public static bool IsLink(string path) => File.Exists(path) && new FileInfo(path).LinkTarget is not null;

    private static string Resolve(string path) =>
        new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;

    internal sealed record CacheEntry(
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("written")] long WrittenTicks,
        [property: JsonPropertyName("etag")] string ETag,
        [property: JsonPropertyName("variant")] string? Variant = null);

    private Dictionary<string, CacheEntry> LoadCache()
    {
        Dictionary<string, CacheEntry>? cache = null;
        try
        {
            cache = File.Exists(cachePath) ? JsonSerializer.Deserialize(File.ReadAllText(cachePath), CoreJson.Default.DictionaryStringCacheEntry) : null;
        }
        catch (JsonException)
        {
            cache = null;
        }
        return cache ?? new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache(Dictionary<string, CacheEntry> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, CoreJson.Default.DictionaryStringCacheEntry));
    }

    // Whether the local file is exactly the remote one: by size, then the cache, then hashing it.
    private async Task<bool> IsCurrentAsync(string path, RemoteFile remote, Dictionary<string, CacheEntry> cache,
        IProgress<ReforgedProgress>? progress, int index, int count, CancellationToken cancel)
    {
        var target = File.Exists(path) ? Resolve(path) : null;
        var info = target is null ? null : new FileInfo(target);
        var matchesSize = info is { Exists: true } && info.Length == remote.Size;
        var cached = matchesSize ? cache.GetValueOrDefault(target!) : null;
        var current = false;
        if (matchesSize && cached is not null && cached.Size == info!.Length && cached.WrittenTicks == info.LastWriteTimeUtc.Ticks)
        {
            current = cached.ETag == remote.ETag;
        }
        else if (matchesSize)
        {
            var name = Path.GetFileName(path);
            var hashProgress = new Progress<long>(done => progress?.Report(new ReforgedProgress(name, "Checking", done, remote.Size, index, count)));
            current = await S3ETag.MatchesAsync(target!, remote.ETag, hashProgress, cancel);
            if (current)
            {
                cache[target!] = new CacheEntry(info!.Length, info.LastWriteTimeUtc.Ticks, remote.ETag);
            }
        }
        return current;
    }

    public async Task<ReforgedStatus> CheckAsync(string gameFolder, IProgress<ReforgedProgress>? progress = null, CancellationToken cancel = default)
    {
        var paths = ReforgedCatalog.Parts.SelectMany(p => p.Variants).SelectMany(v => v.BucketPaths).Distinct().ToList();
        var remote = new Dictionary<string, RemoteFile>();
        foreach (var path in paths)
        {
            remote[path] = await client.HeadAsync(path, cancel);
        }
        var versions = await client.GetVersionsAsync(cancel);
        var cache = LoadCache();
        var parts = new List<PartStatus>();
        var index = 0;
        foreach (var part in ReforgedCatalog.Parts)
        {
            index++;
            var inData = part.Files.Where(f => File.Exists(Path.Combine(DataFolder(gameFolder), f))).ToList();
            var parked = inData.Count == 0 && part.Files.Any(f => File.Exists(Path.Combine(DisabledFolder(gameFolder), f)));
            var data = parked ? DisabledFolder(gameFolder) : DataFolder(gameFolder);
            var present = parked ? part.Files.Where(f => File.Exists(Path.Combine(data, f))).ToList() : inData;
            var linked = present.Any(f => IsLink(Path.Combine(data, f)));
            string? matchedVariant = null;
            var currentFiles = new Dictionary<string, bool>();
            foreach (var variant in part.Variants)
            {
                var allCurrent = present.Count == part.Files.Count;
                for (var i = 0; i < part.Files.Count && allCurrent; i++)
                {
                    allCurrent = await IsCurrentAsync(Path.Combine(data, part.Files[i]), remote[variant.BucketPaths[i]], cache,
                        progress, index, ReforgedCatalog.Parts.Count, cancel);
                }
                matchedVariant ??= allCurrent ? variant.Id : null;
            }
            var recorded = present.Count > 0 ? cache.GetValueOrDefault(Resolve(Path.Combine(data, part.Files[0])))?.Variant : null;
            var installedVariant = matchedVariant ?? recorded ?? part.Variants[0].Id;
            var installed = part.Variants.First(v => v.Id == installedVariant);
            for (var i = 0; i < part.Files.Count; i++)
            {
                var file = Path.Combine(data, part.Files[i]);
                currentFiles[part.Files[i]] = File.Exists(file) && (matchedVariant is not null
                    || await IsCurrentAsync(file, remote[installed.BucketPaths[i]], cache, progress, index, ReforgedCatalog.Parts.Count, cancel));
            }
            var state = present.Count == 0 ? ReforgedPartState.NotInstalled
                : matchedVariant is not null ? ReforgedPartState.Current
                : present.Count < part.Files.Count ? ReforgedPartState.Partial
                : ReforgedPartState.UpdateAvailable;
            var targets = present.Where(f => IsLink(Path.Combine(data, f))).ToDictionary(f => f, f => Resolve(Path.Combine(data, f)));
            parts.Add(new PartStatus(part, state, present.Count == 0 ? null : installedVariant, linked, currentFiles, targets, parked));
        }
        SaveCache(cache);
        var sharedFolders = parts.SelectMany(p => p.Targets?.Values ?? []).Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ReforgedStatus(parts, remote, versions, sharedFolders.Count == 1 ? sharedFolders[0] : null);
    }

    // What changing to the selection takes. Switching a part off parks its files (a link moves, never what it points
    // to) and switching it on brings them back, downloading only what's missing or outdated. A linked file is updated
    // where it points, and a linked part's variant is never switched. A new part follows the shared folder the other
    // parts link to: a copy already there is linked, anything else is downloaded there and then linked.
    public static ReforgedPlan Plan(ReforgedStatus status, IReadOnlyDictionary<string, string?> selection)
    {
        var downloads = new List<ReforgedDownload>();
        var parks = new List<string>();
        var unparks = new List<string>();
        var links = new List<ReforgedLink>();
        foreach (var part in status.Parts)
        {
            var wanted = selection.GetValueOrDefault(part.Part.Id);
            var keepVariant = part.Linked && wanted is not null ? part.InstalledVariant ?? wanted : wanted;
            var variant = keepVariant is null ? null : part.Part.Variants.First(v => v.Id == keepVariant);
            var sameVariant = part.InstalledVariant == keepVariant;
            for (var i = 0; i < part.Part.Files.Count; i++)
            {
                var file = part.Part.Files[i];
                var link = part.Targets?.GetValueOrDefault(file);
                var shared = part.State == ReforgedPartState.NotInstalled && status.SharedFolder is { } folder ? Path.Combine(folder, file) : null;
                if (variant is null && part.IsOn)
                {
                    parks.Add(file);
                }
                else if (variant is not null && part.Parked)
                {
                    unparks.Add(file);
                }
                if (variant is not null && shared is not null && File.Exists(shared))
                {
                    links.Add(new ReforgedLink(file, shared));
                }
                else if (variant is not null && !(sameVariant && part.CurrentFiles.GetValueOrDefault(file)))
                {
                    downloads.Add(new ReforgedDownload(file, variant.BucketPaths[i], status.Remote[variant.BucketPaths[i]], link ?? shared,
                        CreateLink: shared is not null));
                }
            }
        }
        return new ReforgedPlan(downloads, parks, unparks, links);
    }

    // Downloads every file to <name>.part beside where it goes (Data, or the shared folder), checks it against the
    // bucket's ETag, then moves it into place; parking, unparking and linking happen only after all downloads
    // succeeded. Space is checked first, on each drive the downloads go to.
    public async Task ApplyAsync(string gameFolder, ReforgedPlan plan, IReadOnlyDictionary<string, string?> selection,
        IProgress<ReforgedProgress>? progress = null, CancellationToken cancel = default, Func<bool>? gameRunning = null)
    {
        var data = DataFolder(gameFolder);
        string TargetOf(ReforgedDownload d) => d.Target ?? Path.Combine(data, d.File);
        var shortDrive = plan.Downloads
            .GroupBy(d => Path.GetPathRoot(Path.GetFullPath(TargetOf(d)))!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Root: g.Key, Needed: g.Sum(d => d.Remote.Size - (File.Exists(TargetOf(d) + ".part") ? new FileInfo(TargetOf(d) + ".part").Length : 0)),
                Free: new DriveInfo(g.Key).AvailableFreeSpace))
            .FirstOrDefault(d => d.Needed > d.Free);
        if (shortDrive.Root is not null)
        {
            throw new IOException($"Not enough disk space on {shortDrive.Root}: the download needs {Format(shortDrive.Needed)} and the drive has {Format(shortDrive.Free)} free.");
        }
        else
        {
            Directory.CreateDirectory(data);
            var cache = LoadCache();
            for (var i = 0; i < plan.Downloads.Count; i++)
            {
                var download = plan.Downloads[i];
                var target = TargetOf(download);
                var part = target + ".part";
                var index = i + 1;
                await client.DownloadAsync(download.BucketPath, part, download.Remote,
                    new Progress<long>(done => progress?.Report(new ReforgedProgress(download.File, "Downloading", done, download.Remote.Size, index, plan.Downloads.Count))),
                    cancel);
                var verified = new FileInfo(part).Length == download.Remote.Size && await S3ETag.MatchesAsync(part, download.Remote.ETag,
                    new Progress<long>(done => progress?.Report(new ReforgedProgress(download.File, "Verifying", done, download.Remote.Size, index, plan.Downloads.Count))),
                    cancel);
                if (!verified)
                {
                    File.Delete(part);
                    throw new InvalidDataException($"{download.File} didn't match the server's copy after downloading, so it was discarded. Try again.");
                }
            }
            // Only once every file is downloaded and checked do they go into place together, so a stop or an error
            // never leaves a part (like the world set's three files) half old and half new.
            if (gameRunning?.Invoke() == true)
            {
                throw new IOException("The game was started during the download. Close it and press Apply again; the downloaded files are kept.");
            }
            var disabled = DisabledFolder(gameFolder);
            foreach (var file in plan.Unparks.Where(f => File.Exists(Path.Combine(disabled, f))))
            {
                Move(Path.Combine(disabled, file), Path.Combine(data, file), cache);
            }
            foreach (var download in plan.Downloads)
            {
                var target = TargetOf(download);
                File.Move(target + ".part", target, overwrite: true);
                var info = new FileInfo(target);
                var owner = ReforgedCatalog.Parts.First(p => p.Files.Contains(download.File));
                cache[info.FullName] = new CacheEntry(info.Length, info.LastWriteTimeUtc.Ticks, download.Remote.ETag,
                    selection.GetValueOrDefault(owner.Id));
                if (download.CreateLink)
                {
                    LinkOrCopy(Path.Combine(data, download.File), target);
                }
            }
            foreach (var link in plan.Links)
            {
                LinkOrCopy(Path.Combine(data, link.File), link.Target);
            }
            SaveCache(cache);
            var downloading = plan.Downloads.Select(d => d.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var leftover in ReforgedCatalog.Parts.SelectMany(p => p.Files).Where(f => !downloading.Contains(f)))
            {
                File.Delete(Path.Combine(data, leftover + ".part"));
            }
            if (plan.Parks.Count > 0)
            {
                Directory.CreateDirectory(disabled);
            }
            foreach (var file in plan.Parks.Where(f => File.Exists(Path.Combine(data, f))))
            {
                Move(Path.Combine(data, file), Path.Combine(disabled, file), cache);
            }
            SaveCache(cache);
        }
    }

    // Moving a link moves the link itself; the shared file stays where it is. A plain file's hash result follows it.
    private static void Move(string from, string to, Dictionary<string, CacheEntry> cache)
    {
        var linked = IsLink(from);
        File.Move(from, to, overwrite: true);
        if (!linked && cache.Remove(Path.GetFullPath(from), out var entry))
        {
            cache[Path.GetFullPath(to)] = entry;
        }
    }

    // A symbolic link needs Developer Mode or admin rights; without them Data gets a copy of the shared file instead.
    private static void LinkOrCopy(string path, string target)
    {
        File.Delete(path);
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Copy(target, path, overwrite: true);
        }
    }

    public static string Format(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : bytes >= 1_000_000 ? $"{bytes / 1e6:0} MB" : $"{bytes / 1e3:0} KB";
}
