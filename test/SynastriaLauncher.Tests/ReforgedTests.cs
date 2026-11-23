// Covers Project Reforged installs against a fake download bucket: ETag checks (plain and multipart), the
// downloads page's combination rules, installing, resuming, switching variants, removing, refusing a corrupted
// download, updating through links, and the patches its parts need or Reforged recommends.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SynastriaLauncher.Core.Patching;
using SynastriaLauncher.Core.Reforged;

namespace SynastriaLauncher.Tests;

public sealed class ReforgedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-reforged-").FullName;
    private string Game => Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
    private string Cache => Path.Combine(_root, "reforged.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A bucket serving each catalog path with small distinct bytes and a plain MD5 ETag, plus ranged GETs.
    private sealed class FakeBucket : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public Dictionary<string, string> ETags { get; } = new();
        public List<string> Requests { get; } = [];
        public bool Corrupt { get; set; }

        public FakeBucket()
        {
            var paths = ReforgedCatalog.Parts.SelectMany(p => p.Variants).SelectMany(v => v.BucketPaths).Distinct();
            foreach (var path in paths)
            {
                Set(path, System.Text.Encoding.UTF8.GetBytes($"reforged {path} " + new string('x', 200 + path.Length * 7)));
            }
        }

        public void Set(string path, byte[] bytes)
        {
            Files[path] = bytes;
            ETags[path] = Convert.ToHexStringLower(MD5.HashData(bytes));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var path = url.StartsWith(ReforgedCatalog.BucketBase) ? url[ReforgedCatalog.BucketBase.Length..] : null;
            HttpResponseMessage response;
            if (path is null)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<span class=\"dl-version\">v1.5.8</span><a href=\"" + ReforgedCatalog.BucketBase + "patches/patch-A.mpq\">"),
                };
            }
            else
            {
                var bytes = Files[path];
                var range = request.Headers.Range?.Ranges.First().From;
                var ifRange = request.Headers.IfRange?.EntityTag?.Tag.Trim('"');
                var partial = request.Method == HttpMethod.Get && range is > 0 && ifRange == ETags[path];
                Requests.Add($"{request.Method} {path}{(partial ? $" from {range}" : "")}");
                var body = partial ? bytes[(int)range!.Value..] : bytes;
                body = Corrupt && request.Method == HttpMethod.Get ? body.Select(b => (byte)(b ^ 1)).ToArray() : body;
                response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : body),
                };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{ETags[path]}\"");
                response.Content.Headers.ContentLength = request.Method == HttpMethod.Head ? bytes.Length : body.Length;
            }
            return Task.FromResult(response);
        }
    }

    private (ReforgedService Service, FakeBucket Bucket, ReforgedClient Client) Setup()
    {
        var bucket = new FakeBucket();
        var client = new ReforgedClient(bucket);
        return (new ReforgedService(client, Cache), bucket, client);
    }

    private static Dictionary<string, string?> Core() => ReforgedCatalog.Parts.ToDictionary(p => p.Id, p => p.DefaultOn ? p.Variants[0].Id : null);

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // A plain ETag is the file's MD5.
    [Fact]
    public async Task PlainETagIsMd5()
    {
        var path = Path.Combine(_root, "a.bin");
        await File.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);
        Assert.True(await S3ETag.MatchesAsync(path, "5d41402abc4b2a76b9719d911017c592", cancel: TestContext.Current.CancellationToken));
        Assert.False(await S3ETag.MatchesAsync(path, "00000000000000000000000000000000", cancel: TestContext.Current.CancellationToken));
    }

    // A multipart ETag is the MD5 of the parts' MD5s with the part count, found without being told the part size.
    [Fact]
    public async Task MultipartETagMatches()
    {
        var bytes = Random(11 * 1024 * 1024 + 123, 7);
        var path = Path.Combine(_root, "big.bin");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        const int part = 5 * 1024 * 1024;
        var partHashes = Enumerable.Range(0, 3).SelectMany(i => MD5.HashData(bytes.AsSpan(i * part, Math.Min(part, bytes.Length - i * part)))).ToArray();
        var etag = $"{Convert.ToHexStringLower(MD5.HashData(partHashes))}-3";
        Assert.Contains(5L * 1024 * 1024, S3ETag.CandidatePartSizes(bytes.Length, etag));
        Assert.True(await S3ETag.MatchesAsync(path, $"\"{etag}\"", cancel: TestContext.Current.CancellationToken));
        Assert.False(await S3ETag.MatchesAsync(path, etag.Replace("-3", "-4"), cancel: TestContext.Current.CancellationToken));
    }

    // The downloads page's rules: Creatures needs Characters, Upright Orcs needs both, standard sounds need Maps.
    [Fact]
    public void EnforcesCombinationRules()
    {
        Assert.Empty(ReforgedCatalog.Problems(Core()));
        var noCharacters = Core();
        noCharacters["characters"] = null;
        Assert.Single(ReforgedCatalog.Problems(noCharacters));
        var orcs = Core();
        orcs["creatures"] = null;
        orcs["orcs"] = "on";
        Assert.Single(ReforgedCatalog.Problems(orcs));
        var sounds = Core();
        sounds["sounds"] = "standard";
        Assert.Single(ReforgedCatalog.Problems(sounds));
        sounds["sounds"] = "standalone";
        Assert.Empty(ReforgedCatalog.Problems(sounds));
    }

    // A fresh install downloads the core parts into Data, after which they read as current and nothing is left to do.
    [Fact]
    public async Task InstallsAndThenReadsCurrent()
    {
        var (service, _, client) = Setup();
        using var _c = client;
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.False(status.AnyInstalled);
        var plan = ReforgedService.Plan(status, Core());
        Assert.Equal(7, plan.Downloads.Count);
        await service.ApplyAsync(Game, plan, Core(), cancel: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(Game, "Data", "patch-E.mpq")));
        var after = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(ReforgedPartState.Current, after.Get("world").State);
        Assert.True(ReforgedService.Plan(after, after.CurrentSelection()).IsEmpty);
    }

    // A file the server changed reads as an update, and only that file is downloaded again.
    [Fact]
    public async Task UpdatesOnlyChangedFiles()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, Core()), Core(), cancel: TestContext.Current.CancellationToken);
        bucket.Set("patches/patch-G.mpq", Random(700, 3));
        var after = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(ReforgedPartState.UpdateAvailable, after.Get("gear").State);
        var plan = ReforgedService.Plan(after, after.CurrentSelection());
        Assert.Equal(["patch-G.mpq"], plan.Downloads.Select(d => d.File));
    }

    // An interrupted download resumes from its .part file with a ranged request.
    [Fact]
    public async Task ResumesPartialDownloads()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var data = Directory.CreateDirectory(Path.Combine(Game, "Data")).FullName;
        var full = bucket.Files["patches/patch-I.mpq"];
        await File.WriteAllBytesAsync(Path.Combine(data, "patch-I.mpq.part"), full[..100], TestContext.Current.CancellationToken);
        var selection = ReforgedCatalog.Parts.ToDictionary(p => p.Id, p => p.Id == "interface" ? "on" : (string?)null);
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, selection), selection, cancel: TestContext.Current.CancellationToken);
        Assert.Contains("GET patches/patch-I.mpq from 100", bucket.Requests);
        Assert.Equal(full, await File.ReadAllBytesAsync(Path.Combine(data, "patch-I.mpq"), TestContext.Current.CancellationToken));
    }

    // Switching Characters from HD to SD downloads the other file; switching a part off parks only its files.
    [Fact]
    public async Task SwitchesVariantsAndParks()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, Core()), Core(), cancel: TestContext.Current.CancellationToken);
        var sd = Core();
        sd["characters"] = "sd";
        sd["gear"] = null;
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        var plan = ReforgedService.Plan(status, sd);
        Assert.Equal(["patch-A.mpq"], plan.Downloads.Select(d => d.File));
        Assert.Equal(["patch-G.mpq"], plan.Parks);
        await service.ApplyAsync(Game, plan, sd, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(bucket.Files["patches/extras/patch-A.mpq"], await File.ReadAllBytesAsync(Path.Combine(Game, "Data", "patch-A.mpq"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(Game, "Data", "patch-G.mpq")));
        Assert.True(File.Exists(Path.Combine(Game, "Data", "_Disabled", "patch-G.mpq")));
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.Equal("sd", status.Get("characters").InstalledVariant);
        Assert.True(status.Get("gear").Parked);
        Assert.Null(status.CurrentSelection()["gear"]);
    }

    // Switching a parked part back on moves its file back without downloading anything, and it reads current.
    [Fact]
    public async Task SwitchesBackOnWithoutDownloading()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, Core()), Core(), cancel: TestContext.Current.CancellationToken);
        var off = Core();
        off["gear"] = null;
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, off), off, cancel: TestContext.Current.CancellationToken);
        bucket.Requests.Clear();
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        var plan = ReforgedService.Plan(status, Core());
        Assert.Empty(plan.Downloads);
        Assert.Equal(["patch-G.mpq"], plan.Unparks);
        await service.ApplyAsync(Game, plan, Core(), cancel: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(bucket.Requests, r => r.StartsWith("GET"));
        Assert.Equal(bucket.Files["patches/patch-G.mpq"], await File.ReadAllBytesAsync(Path.Combine(Game, "Data", "patch-G.mpq"), TestContext.Current.CancellationToken));
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(ReforgedPartState.Current, status.Get("gear").State);
        Assert.False(status.Get("gear").Parked);
    }

    // A parked part that Reforged updated meanwhile is moved back and then brought up to date.
    [Fact]
    public async Task SwitchesBackOnAndUpdates()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, Core()), Core(), cancel: TestContext.Current.CancellationToken);
        var off = Core();
        off["gear"] = null;
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, off), off, cancel: TestContext.Current.CancellationToken);
        bucket.Set("patches/patch-G.mpq", Random(800, 4));
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(ReforgedPartState.UpdateAvailable, status.Get("gear").State);
        var plan = ReforgedService.Plan(status, Core());
        Assert.Equal(["patch-G.mpq"], plan.Downloads.Select(d => d.File));
        await service.ApplyAsync(Game, plan, Core(), cancel: TestContext.Current.CancellationToken);
        Assert.Equal(bucket.Files["patches/patch-G.mpq"], await File.ReadAllBytesAsync(Path.Combine(Game, "Data", "patch-G.mpq"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(Game, "Data", "_Disabled", "patch-G.mpq")));
    }

    // A download that doesn't match the server's ETag is discarded and the old file stays.
    [Fact]
    public async Task RefusesCorruptedDownloads()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var data = Directory.CreateDirectory(Path.Combine(Game, "Data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "patch-P.mpq"), "old", TestContext.Current.CancellationToken);
        bucket.Corrupt = true;
        var selection = ReforgedCatalog.Parts.ToDictionary(p => p.Id, p => p.Id == "particles" ? "on" : (string?)null);
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyAsync(Game, ReforgedService.Plan(status, selection), selection, cancel: TestContext.Current.CancellationToken));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(data, "patch-P.mpq"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(data, "patch-P.mpq.part")));
    }

    // Puts the given bytes in a shared folder and links Data\<file> to it, the way the user's clients share Reforged.
    private string LinkShared(string file, byte[] bytes)
    {
        var shared = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "shared")).FullName, file);
        File.WriteAllBytes(shared, bytes);
        var data = Directory.CreateDirectory(Path.Combine(Game, "Data")).FullName;
        var linked = true;
        try
        {
            File.CreateSymbolicLink(Path.Combine(data, file), shared);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            linked = false;
        }
        Assert.SkipUnless(linked, "Creating symbolic links needs Developer Mode or admin rights");
        return shared;
    }

    // An outdated linked file is downloaded into the shared folder it points to, and the link in Data stays a link.
    [Fact]
    public async Task UpdatesThroughLinks()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var shared = LinkShared("patch-G.mpq", Random(500, 9));
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.True(status.Get("gear").Linked);
        Assert.Equal(ReforgedPartState.UpdateAvailable, status.Get("gear").State);
        var plan = ReforgedService.Plan(status, status.CurrentSelection());
        var download = Assert.Single(plan.Downloads);
        Assert.Equal(shared, download.Target);
        await service.ApplyAsync(Game, plan, status.CurrentSelection(), cancel: TestContext.Current.CancellationToken);
        Assert.Equal(bucket.Files["patches/patch-G.mpq"], await File.ReadAllBytesAsync(shared, TestContext.Current.CancellationToken));
        Assert.True(ReforgedService.IsLink(Path.Combine(Game, "Data", "patch-G.mpq")));
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(ReforgedPartState.Current, status.Get("gear").State);
    }

    // Switching a linked part off parks only the link, which still points at the shared file; switching it on moves
    // the link back.
    [Fact]
    public async Task ParksLinksNotSharedFiles()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var shared = LinkShared("patch-G.mpq", bucket.Files["patches/patch-G.mpq"]);
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        var off = status.CurrentSelection();
        off["gear"] = null;
        var plan = ReforgedService.Plan(status, off);
        Assert.Equal(["patch-G.mpq"], plan.Parks);
        await service.ApplyAsync(Game, plan, off, cancel: TestContext.Current.CancellationToken);
        var parked = Path.Combine(Game, "Data", "_Disabled", "patch-G.mpq");
        Assert.False(File.Exists(Path.Combine(Game, "Data", "patch-G.mpq")));
        Assert.True(ReforgedService.IsLink(parked));
        Assert.Equal(bucket.Files["patches/patch-G.mpq"], await File.ReadAllBytesAsync(shared, TestContext.Current.CancellationToken));
        status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        Assert.True(status.Get("gear") is { Parked: true, Linked: true });
        var on = status.CurrentSelection();
        on["gear"] = "on";
        await service.ApplyAsync(Game, ReforgedService.Plan(status, on), on, cancel: TestContext.Current.CancellationToken);
        Assert.True(ReforgedService.IsLink(Path.Combine(Game, "Data", "patch-G.mpq")));
    }

    // With the other parts linked to a shared folder, a new part whose file is already there is linked, not downloaded.
    [Fact]
    public async Task LinksANewPartFromTheSharedFolder()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var gear = LinkShared("patch-G.mpq", bucket.Files["patches/patch-G.mpq"]);
        var particles = Path.Combine(Path.GetDirectoryName(gear)!, "patch-P.mpq");
        await File.WriteAllBytesAsync(particles, bucket.Files["patches/patch-P.mpq"], TestContext.Current.CancellationToken);
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        var on = status.CurrentSelection();
        on["particles"] = "on";
        var plan = ReforgedService.Plan(status, on);
        Assert.Empty(plan.Downloads);
        Assert.Equal([new ReforgedLink("patch-P.mpq", particles)], plan.Links);
        await service.ApplyAsync(Game, plan, on, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(particles, new FileInfo(Path.Combine(Game, "Data", "patch-P.mpq")).LinkTarget);
    }

    // A new part missing from the shared folder is downloaded there and then linked into Data.
    [Fact]
    public async Task DownloadsANewPartToTheSharedFolder()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var gear = LinkShared("patch-G.mpq", bucket.Files["patches/patch-G.mpq"]);
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        var on = status.CurrentSelection();
        on["interface"] = "on";
        var download = Assert.Single(ReforgedService.Plan(status, on).Downloads);
        var shared = Path.Combine(Path.GetDirectoryName(gear)!, "patch-I.mpq");
        Assert.Equal(shared, download.Target);
        Assert.True(download.CreateLink);
        await service.ApplyAsync(Game, ReforgedService.Plan(status, on), on, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(bucket.Files["patches/patch-I.mpq"], await File.ReadAllBytesAsync(shared, TestContext.Current.CancellationToken));
        Assert.Equal(shared, new FileInfo(Path.Combine(Game, "Data", "patch-I.mpq")).LinkTarget);
    }

    // Clean Up lists a switched-off part, and removing a parked link leaves the shared file alone.
    [Fact]
    public void CleanUpListsParkedPartsAndKeepsSharedFiles()
    {
        var shared = LinkShared("patch-G.mpq", Random(300, 2));
        var disabled = Directory.CreateDirectory(Path.Combine(Game, "Data", "_Disabled")).FullName;
        File.Move(Path.Combine(Game, "Data", "patch-G.mpq"), Path.Combine(disabled, "patch-G.mpq"));
        File.WriteAllBytes(Path.Combine(disabled, "patch-P.mpq"), Random(200, 5));
        var items = SynastriaLauncher.Core.Backups.Find(Game, [], "DLLs");
        Assert.Equal(2, items.Count);
        SynastriaLauncher.Core.Backups.Recycle(items.Where(i => i.Path.EndsWith("patch-G.mpq")));
        Assert.False(File.Exists(Path.Combine(disabled, "patch-G.mpq")));
        Assert.True(File.Exists(shared));
    }

    // A linked part's variant switches where the link points: SD over a linked HD file downloads into the shared copy.
    [Fact]
    public async Task SwitchesLinkedVariantInSharedFolder()
    {
        var (service, bucket, client) = Setup();
        using var _c = client;
        var shared = LinkShared("patch-A.mpq", bucket.Files["patches/patch-A.mpq"]);
        var status = await service.CheckAsync(Game, cancel: TestContext.Current.CancellationToken);
        var sd = status.CurrentSelection();
        sd["characters"] = "sd";
        var download = Assert.Single(ReforgedService.Plan(status, sd).Downloads, d => d.File == "patch-A.mpq");
        Assert.Equal("patches/extras/patch-A.mpq", download.BucketPath);
        Assert.Equal(Path.GetFullPath(shared), Path.GetFullPath(download.Target!));
        Assert.False(download.CreateLink);
    }

    // The Interface part needs Interface Edit; with it off that's the one missing patch.
    [Fact]
    public void ListsMissingPatches()
    {
        var interfacePart = ReforgedCatalog.Get("interface");
        var only = Assert.Single(ReforgedCatalog.MissingPatches([ReforgedCatalog.Get("characters"), interfacePart], []));
        Assert.Equal(PatchIds.InterfaceEdit, only.Patch);
        Assert.Equal([interfacePart], only.Parts);
    }

    // Nothing is missing once the needed patches are on, or when no part is in use.
    [Fact]
    public void NoMissingPatchesWhenOnOrUnused()
    {
        Assert.Empty(ReforgedCatalog.MissingPatches(ReforgedCatalog.Parts.ToList(), [PatchIds.InterfaceEdit]));
        Assert.Empty(ReforgedCatalog.MissingPatches([], []));
    }

    // A part counts as installed when one of its files is in Data.
    [Fact]
    public void FindsInstalledParts()
    {
        Directory.CreateDirectory(Path.Combine(Game, "Data"));
        File.WriteAllText(Path.Combine(Game, "Data", "patch-I.mpq"), "");
        Assert.Equal(["interface"], ReforgedCatalog.InstalledParts(Game).Select(p => p.Id));
    }
}
