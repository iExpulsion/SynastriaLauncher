// Covers updating Synastria's client files: finding the package link on synastria.org and the file on MediaFire's
// page, reading a package, planning which files change (WoWExt.bin kept, WowExt.exe compared with its original),
// installing with backups, and, given an unpatched WowExt.exe, putting the launcher's patches back on a new exe.

using System.IO.Compression;
using System.Net;
using System.Text;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Client;
using SynastriaLauncher.Core.GitHub;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class WowExtUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-wowext-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Game => Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;

    private static readonly Dictionary<string, byte[]> V54 = new()
    {
        ["WowExt.exe"] = "exe 54"u8.ToArray(),
        ["WoWExt.dll"] = "dll 54"u8.ToArray(),
        ["WoWExt.bin"] = "cache from the package"u8.ToArray(),
        ["WoWExt2.bin"] = "items 54"u8.ToArray(),
        ["WoWExt.readme.txt"] = "readme"u8.ToArray(),
    };

    private string MakeZip(IReadOnlyDictionary<string, byte[]> files, string name = "WoWExt_v54.zip")
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (file, bytes) in files)
        {
            using var stream = zip.CreateEntry(file).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private void InstallFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(Game, name), bytes);
        }
    }

    // The site's link gives the version and URL (HTML entities decoded); the highest version wins if several are linked.
    [Fact]
    public void ParsesSiteLink()
    {
        var html = """<a href="https://www.mediafire.com/file/abc/WoWExt_v53.zip/file">old</a> <a class="x" href='https://www.mediafire.com/file/def/WoWExt_v54.zip/file?a=1&amp;b=2'>new</a>""";
        var release = WowExtUpdate.ParseSite(html)!;
        Assert.Equal(54, release.Version);
        Assert.Equal("https://www.mediafire.com/file/def/WoWExt_v54.zip/file?a=1&b=2", release.Url);
        Assert.Null(WowExtUpdate.ParseSite("<a href=\"/downloads\">Downloads</a>"));
    }

    // MediaFire's page carries the direct download link; file names give the version.
    [Fact]
    public void ParsesMediaFirePageAndNames()
    {
        var html = """<a aria-label="Download file" href="https://download2303.mediafire.com/xyz/abc/WoWExt_v54.zip" id="downloadButton">""";
        Assert.Equal("https://download2303.mediafire.com/xyz/abc/WoWExt_v54.zip", WowExtUpdate.ParseMediaFire(html));
        Assert.Null(WowExtUpdate.ParseMediaFire("<html>captcha</html>"));
        Assert.Equal(55, WowExtUpdate.VersionFromName("WoWExt_v55 (1).zip"));
        Assert.Null(WowExtUpdate.VersionFromName("wowext.zip"));
    }

    // Serves synastria.org, a MediaFire page and the file itself.
    private sealed class FakeWeb(string zipPath) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            HttpContent? content = url switch
            {
                WowExtUpdate.SiteUrl => new StringContent("""<a href="https://www.mediafire.com/file/abc/WoWExt_v54.zip/file">Download</a>"""),
                "https://www.mediafire.com/file/abc/WoWExt_v54.zip/file" => new StringContent("""<a href="https://download1.mediafire.com/k/abc/WoWExt_v54.zip">Download</a>"""),
                "https://download1.mediafire.com/k/abc/WoWExt_v54.zip" => new ByteArrayContent(File.ReadAllBytes(zipPath)),
                _ => null,
            };
            return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content });
        }
    }

    // A check reads the site's link, and a download goes through MediaFire's page to the file.
    [Fact]
    public async Task ChecksAndDownloadsThroughMediaFire()
    {
        var zip = MakeZip(V54);
        using var web = new FakeWeb(zip);
        using var http = new GitHubKeyless(web);
        var release = (await WowExtUpdate.CheckAsync(http))!;
        Assert.Equal(54, release.Version);
        var saved = Path.Combine(_root, "saved.zip");
        await WowExtUpdate.DownloadAsync(http, release, saved);
        Assert.Equal(File.ReadAllBytes(zip), File.ReadAllBytes(saved));
        Assert.Contains("https://www.mediafire.com/file/abc/WoWExt_v54.zip/file", web.Requests);
    }

    // A zip without WowExt.exe and WoWExt.dll, or not a zip at all, is refused.
    [Fact]
    public void RefusesOtherZips()
    {
        var other = MakeZip(new Dictionary<string, byte[]> { ["readme.txt"] = "x"u8.ToArray() }, "other.zip");
        Assert.Contains("WowExt.exe and WoWExt.dll", Assert.Throws<InvalidDataException>(() => WowExtUpdate.ReadPackage(other)).Message);
        var junk = Path.Combine(_root, "junk.zip");
        File.WriteAllText(junk, "not a zip");
        Assert.Throws<InvalidDataException>(() => WowExtUpdate.ReadPackage(junk));
    }

    // A client with the package's files is current even though its WoWExt.bin differs; the readme doesn't count.
    [Fact]
    public void MatchingClientIsCurrent()
    {
        InstallFiles(V54.Where(f => !f.Key.EndsWith(".txt")).ToDictionary());
        File.WriteAllText(Path.Combine(Game, "WoWExt.bin"), "the server's newer cache");
        var plan = WowExtUpdate.Plan(WowExtUpdate.ReadPackage(MakeZip(V54)), Game, 54);
        Assert.True(plan.IsCurrent);
        Assert.DoesNotContain(plan.Files, f => f.Name.EndsWith(".txt"));
    }

    // WowExt.exe is compared with the kept original, not with the patched exe.
    [Fact]
    public void ComparesExeWithOriginal()
    {
        InstallFiles(V54);
        File.WriteAllText(Path.Combine(Game, "WowExt.exe"), "patched by the launcher");
        File.WriteAllBytes(Path.Combine(Game, "WowExt.exe.bak"), V54["WowExt.exe"]);
        Assert.True(WowExtUpdate.Plan(WowExtUpdate.ReadPackage(MakeZip(V54)), Game, 54).IsCurrent);
    }

    // Changed and missing files are written, the old ones go to _WowExtBackups\v53, and WoWExt.bin is left alone.
    [Fact]
    public void InstallsChangedFilesWithBackup()
    {
        InstallFiles(V54);
        File.WriteAllText(Path.Combine(Game, "WoWExt.dll"), "dll 53");
        File.WriteAllText(Path.Combine(Game, "WoWExt.bin"), "the server's cache");
        File.Delete(Path.Combine(Game, "WoWExt2.bin"));
        var package = WowExtUpdate.ReadPackage(MakeZip(V54));
        var plan = WowExtUpdate.Plan(package, Game, 54);
        Assert.Equal(["WoWExt.dll", "WoWExt2.bin"], plan.Updates.Select(f => f.Name).Order());
        Assert.False(plan.ExeChanged);
        Assert.Null(WowExtUpdate.Apply(plan, package, Game, 53, null));
        Assert.Equal("dll 54", File.ReadAllText(Path.Combine(Game, "WoWExt.dll")));
        Assert.Equal("items 54", File.ReadAllText(Path.Combine(Game, "WoWExt2.bin")));
        Assert.Equal("the server's cache", File.ReadAllText(Path.Combine(Game, "WoWExt.bin")));
        Assert.Equal("dll 53", File.ReadAllText(Path.Combine(Game, "_WowExtBackups", "v53", "WoWExt.dll")));
        Assert.True(WowExtUpdate.Plan(package, Game, 54).IsCurrent);
    }

    // A missing WoWExt.bin is written from the package.
    [Fact]
    public void WritesMissingCache()
    {
        InstallFiles(V54.Where(f => f.Key != "WoWExt.bin").ToDictionary());
        var package = WowExtUpdate.ReadPackage(MakeZip(V54));
        var plan = WowExtUpdate.Plan(package, Game, 54);
        Assert.Equal(WowExtFileChange.Missing, Assert.Single(plan.Updates).Change);
        WowExtUpdate.Apply(plan, package, Game, 54, null);
        Assert.Equal("cache from the package", File.ReadAllText(Path.Combine(Game, "WoWExt.bin")));
    }

    // Settings' backup cleanup lists the replaced WowExt files.
    [Fact]
    public void BackupsListOldWowExtFiles()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Game, "_WowExtBackups", "v53")).FullName;
        File.WriteAllText(Path.Combine(folder, "WoWExt.dll"), "12345");
        var found = Backups.Find(Game, [], "DLLs");
        Assert.Contains(found, b => b.Label == "WowExt files from v53" && b.Bytes == 5);
    }

    // A new WowExt.exe becomes the original and WowExt.exe, and the patches that were on go back on it: the DLL
    // Loader with its folder, and the in-memory switches in SynastriaLauncher.ini. The old original is backed up.
    [Fact]
    public void NewExeGetsPatchesBack()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        var config = new PatcherConfig([PatchIds.NoMailDelay], 80, 100, DamageColors.Default);
        byte[] dll = [0x4D, 0x5A, 1];
        exe.ApplyMemory(config, loader: true, "Mods", dll);
        var newExe = (byte[])original.Clone();
        var timestamp = BitConverter.ToInt32(newExe, 0x3C) + 8;
        newExe[timestamp] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(game.Folder, "WoWExt.dll"), "dll"u8.ToArray());
        var package = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["WowExt.exe"] = newExe, ["WoWExt.dll"] = "dll"u8.ToArray() };
        var plan = WowExtUpdate.Plan(package, game.Folder, 55);
        Assert.True(plan.ExeChanged);
        var result = WowExtUpdate.Apply(plan, package, game.Folder, 54, dll)!;
        Assert.Empty(result.Result.Failed);
        Assert.Equal(newExe, File.ReadAllBytes(exe.BackupPath));
        Assert.Contains(PatchIds.DllLoader, GameExe.AppliedIds(exe.Read()));
        Assert.Equal("Mods", ExePatcher.ReadSettings(exe.Read()).DllFolder);
        Assert.Equal(config.On, exe.ReadConfig()!.On);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Folder, "_WowExtBackups", "v54", "WowExt.exe")));
    }
}
