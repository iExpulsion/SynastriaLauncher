// Covers WoW Dungeon Maps: finding the client's language, reading release tags, each part's state (Project
// Reforged's maps standing in for the map patches), what a part still needs, and installing a patch and addons from
// a fake GitHub, pre-release included.

using System.IO.Compression;
using System.Net;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.GitHub;
using SynastriaLauncher.Core.Wdm;

namespace SynastriaLauncher.Tests;

public sealed class WdmTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-wdm-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Game => Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;

    private void Touch(params string[] parts)
    {
        var path = Path.Combine([Game, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private static WdmPart Part(string id) => WdmCatalog.Get(id);

    // The language is the Data subfolder with its locale archive; enUS when there's none.
    [Fact]
    public void FindsClientLanguage()
    {
        Assert.Equal("enUS", WdmCatalog.Locale(Game));
        Directory.CreateDirectory(Path.Combine(Game, "Data", "Interface"));
        Touch("Data", "deDE", "locale-deDE.MPQ");
        Assert.Equal("deDE", WdmCatalog.Locale(Game));
    }

    // With several language folders, the one Config.wtf sets wins over name order.
    [Fact]
    public void ConfigLanguageWinsOverFolderOrder()
    {
        Touch("Data", "enGB", "locale-enGB.MPQ");
        Touch("Data", "enUS", "locale-enUS.MPQ");
        Assert.Equal("enGB", WdmCatalog.Locale(Game));
        Directory.CreateDirectory(Path.Combine(Game, "WTF"));
        File.WriteAllLines(Path.Combine(Game, "WTF", "Config.wtf"), ["SET gxApi \"d3d9\"", "SET locale \"enUS\""]);
        Assert.Equal("enUS", WdmCatalog.Locale(Game));
        File.WriteAllLines(Path.Combine(Game, "WTF", "Config.wtf"), ["SET locale \"frFR\""]);
        Assert.Equal("enGB", WdmCatalog.Locale(Game));
    }

    // Release tags come from the feed's entry links, newest first, without the links inside release notes.
    [Fact]
    public void ParsesReleaseFeed()
    {
        var atom = """
            <entry><link rel="alternate" type="text/html" href="https://github.com/Trimitor/WDM-patch/releases/tag/2.4.5-stable"/>
            <content>&lt;a href="https://github.com/Trimitor/WDM-patch/releases/tag/2.4.2-stable"&gt;main patch&lt;/a&gt;</content></entry>
            <entry><link rel="alternate" type="text/html" href="https://github.com/Trimitor/WDM-patch/releases/tag/2.4.5-beta"/></entry>
            """;
        Assert.Equal(["2.4.5-stable", "2.4.5-beta"], WdmCatalog.ParseReleaseTags(atom));
    }

    // A patch the launcher didn't install reads Version Unknown; a recorded one is current or has an update.
    [Fact]
    public void EvaluatesStates()
    {
        Assert.Equal(WdmState.NotInstalled, WdmService.Evaluate(Part(WdmCatalog.Map), Game, "enUS", null, "2").State);
        Touch("Data", "enUS", "patch-enUS-M.MPQ");
        Assert.Equal(WdmState.VersionUnknown, WdmService.Evaluate(Part(WdmCatalog.Map), Game, "enUS", null, "2").State);
        Assert.Equal(WdmState.UpdateAvailable, WdmService.Evaluate(Part(WdmCatalog.Map), Game, "enUS", "1", "2").State);
        Assert.Equal(WdmState.Installed, WdmService.Evaluate(Part(WdmCatalog.Map), Game, "enUS", "2", "2").State);
        Assert.Equal(WdmState.NotInstalled, WdmService.Evaluate(Part(WdmCatalog.Addon), Game, "enUS", null, "2").State);
    }

    // With Reforged's maps, missing map patches read In Project Reforged, installed ones are conflicts, and Reforged
    // stands in for Dungeon Maps when Caverns and Mines checks what it needs.
    [Fact]
    public void ReforgedMapsCoverThePatches()
    {
        Touch("Data", "patch-M.mpq");
        Assert.Equal(WdmState.InReforged, WdmService.Evaluate(Part(WdmCatalog.Caves), Game, "enUS", null, "2").State);
        Assert.Empty(WdmService.ReforgedConflicts(Game, "enUS"));
        Assert.Equal([WdmCatalog.Addon], WdmService.MissingNeeds(Part(WdmCatalog.Caves), Game, "enUS").Select(p => p.Id));
        Touch("Data", "enUS", "patch-enUS-M.MPQ");
        Assert.Equal([WdmCatalog.Map], WdmService.ReforgedConflicts(Game, "enUS").Select(p => p.Id));
        Assert.Equal(WdmState.VersionUnknown, WdmService.Evaluate(Part(WdmCatalog.Map), Game, "enUS", null, "2").State);
    }

    // Without Reforged, Caverns and Mines needs Dungeon Maps and the WDM addon (which carries its own Astrolabe).
    [Fact]
    public void CavesNeedMapsAndAddon()
    {
        Assert.Equal([WdmCatalog.Map, WdmCatalog.Addon], WdmService.MissingNeeds(Part(WdmCatalog.Caves), Game, "enUS").Select(p => p.Id));
        Touch("Data", "enUS", "patch-enUS-M.MPQ");
        Assert.Equal([WdmCatalog.Addon], WdmService.MissingNeeds(Part(WdmCatalog.Caves), Game, "enUS").Select(p => p.Id));
        Touch("Interface", "AddOns", "WDM", "WDM.toc");
        Assert.Empty(WdmService.MissingNeeds(Part(WdmCatalog.Caves), Game, "enUS"));
    }

    private static byte[] Zip(string folder)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"{folder}/{folder}.toc").Open());
            writer.Write("## Interface: 30300");
        }
        return memory.ToArray();
    }

    // GitHub as the launcher reads it: the latest-release redirect, the releases feed, the assets fragment, files.
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private static readonly Dictionary<string, Dictionary<string, byte[]>> Releases = new()
        {
            ["2.4.5-stable"] = new() { ["patch-enUS-M.MPQ"] = "maps 2.4.5"u8.ToArray() },
            ["2.4.5-beta"] = new() { ["patch-enUS-N.MPQ"] = "caves beta"u8.ToArray() },
            ["1.0.9-stable"] = new() { ["WDM.zip"] = Zip("WDM"), ["Mapster.zip"] = Zip("Mapster") },
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.EndsWith("/releases/latest"))
            {
                response = new HttpResponseMessage(HttpStatusCode.Found);
                var tag = path.Contains("WDM-patch") ? "2.4.5-stable" : "1.0.9-stable";
                response.Headers.Location = new Uri($"https://github.com{path.Replace("/latest", "")}/tag/{tag}");
            }
            else if (path.EndsWith("/releases.atom"))
            {
                response = Ok(string.Join("", new[] { "2.4.5-stable", "2.4.5-beta" }.Select(t =>
                    $"""<entry><link rel="alternate" type="text/html" href="https://github.com/Trimitor/WDM-patch/releases/tag/{t}"/></entry>""")));
            }
            else if (path.Contains("/releases/expanded_assets/") && Releases.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var assets))
            {
                var repo = path[..path.IndexOf("/releases", StringComparison.Ordinal)];
                var tag = path[(path.LastIndexOf('/') + 1)..];
                response = Ok("Source code " + string.Join("", assets.Keys.Select(a => $"""<a href="{repo}/releases/download/{tag}/{a}">""")));
            }
            else if (path.Contains("/releases/download/"))
            {
                var parts = path.Split('/');
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Releases[parts[^2]][parts[^1]]) };
            }
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }

    // A stable part takes the latest release; a pre-release part the newest tag in the feed that has its file.
    [Fact]
    public async Task FindsLatestTags()
    {
        using var http = new GitHubKeyless(new FakeGitHub());
        Assert.Equal("2.4.5-stable", await WdmService.LatestTagAsync(http, Part(WdmCatalog.Map), "enUS"));
        Assert.Equal("2.4.5-beta", await WdmService.LatestTagAsync(http, Part(WdmCatalog.Caves), "enUS"));
        Assert.Equal("1.0.9-stable", await WdmService.LatestTagAsync(http, Part(WdmCatalog.Addon), "enUS"));
    }

    // Installing puts the patch in Data\<locale> and the addon in AddOns; uninstalling removes them.
    [Fact]
    public async Task InstallsAndUninstalls()
    {
        using var http = new GitHubKeyless(new FakeGitHub());
        await WdmService.InstallAsync(http, Part(WdmCatalog.Map), "2.4.5-stable", Game, "enUS", keepBackup: false);
        Assert.Equal("maps 2.4.5", File.ReadAllText(Path.Combine(Game, "Data", "enUS", "patch-enUS-M.MPQ")));
        await WdmService.InstallAsync(http, Part(WdmCatalog.Addon), "1.0.9-stable", Game, "enUS", keepBackup: false);
        Assert.True(WdmService.IsInstalled(Part(WdmCatalog.Addon), Game, "enUS"));
        Assert.True(File.Exists(Path.Combine(GameFolder.AddOnsPath(Game), "WDM", "WDM.toc")));
        WdmService.Uninstall(Part(WdmCatalog.Addon), Game, "enUS");
        WdmService.Uninstall(Part(WdmCatalog.Map), Game, "enUS");
        Assert.False(WdmService.IsInstalled(Part(WdmCatalog.Addon), Game, "enUS"));
        Assert.False(WdmService.IsInstalled(Part(WdmCatalog.Map), Game, "enUS"));
    }

    // A release without the part's file is refused before anything is written.
    [Fact]
    public async Task RefusesMissingAsset()
    {
        using var http = new GitHubKeyless(new FakeGitHub());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WdmService.InstallAsync(http, Part(WdmCatalog.Caves), "2.4.5-stable", Game, "enUS", keepBackup: false));
        Assert.False(Directory.Exists(Path.Combine(Game, "Data")));
    }
}
