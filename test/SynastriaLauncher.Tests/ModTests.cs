// Covers mods without the network: the built-in manifest, installing from file:// zips with hash checks, config
// files, detection and removal, and launcher version tags.

using System.IO.Compression;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.GitHub;
using SynastriaLauncher.Core.Mods;

namespace SynastriaLauncher.Tests;

public sealed class ModTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-mods-").FullName;
    private readonly GitHubKeyless _github = new();

    public ModTests() => ModPresets.CacheFolder = Path.Combine(_root, "presets");

    public void Dispose()
    {
        _github.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Game => Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;

    private string MakeZip(params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid()}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return new Uri(path).AbsoluteUri;
    }

    private static string Sha(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    // The built-in manifest loads, every mod has files, and every file names its source (download, text, bundled or presets).
    [Fact]
    public void EmbeddedManifestLoads()
    {
        var manifest = ModManifest.Embedded();
        Assert.NotEmpty(manifest.Mods);
        Assert.All(manifest.Mods, m => Assert.NotEmpty(m.Files));
        Assert.All(manifest.Mods.SelectMany(m => m.Files),
            f => Assert.True(f.Url is not null || f.Content is not null || f.Bundled is not null || f.Presets is { Count: > 0 }));
        Assert.Equal(manifest.Mods.Count, manifest.Mods.Select(m => m.Id).Distinct().Count());
        Assert.Empty(manifest.Problems());
        Assert.True(LiveList.Supports(manifest, new Version(0, 1, 0)));
    }

    // A published mods list with a file outside the game folder is refused.
    [Fact]
    public void ModFilesMustStayInTheGameFolder()
    {
        var mod = ModManifest.Embedded().Mods[0];
        Assert.NotEmpty(new ModManifest(99, [mod with { Files = [new ModFile("..\\..\\evil.dll", Content: "x")] }]).Problems());
        Assert.NotEmpty(new ModManifest(99, [mod with { Files = [new ModFile("C:\\Windows\\evil.dll", Content: "x")] }]).Problems());
        Assert.NotEmpty(new ModManifest(99, [mod with { Files = [new ModFile("x.dll", Url: "file://server/share/x.dll")] }]).Problems());
        Assert.NotEmpty(new ModManifest(99, [mod with { Files = [new ModFile("x.dll", Url: "http://example.com/x.dll")] }]).Problems());
    }

    // Every bundled file of the Synastria mods ships inside the launcher; other mods bundle files only when the
    // launcher builds them from source.
    [Fact]
    public void BundledFilesAreEmbedded()
    {
        var mods = ModManifest.Embedded().Mods;
        Assert.All(mods.Where(m => m.IsSynastria).SelectMany(m => m.Files).Where(f => f.Bundled is not null),
            f => Assert.NotNull(BundledFiles.Read(f.Bundled!)));
        Assert.All(mods.Where(m => m.Files.Any(f => f.Bundled is not null)), m => Assert.True(m.IsSynastria || m.BuiltFromSource));
    }

    // wow-optimize's DLL is built into the launcher (CI runs tools/build-wow-optimize.ps1 first; skips locally until
    // then); its settings file comes from presets, not the bundle.
    [Fact]
    public void WowOptimizeIsBuiltIn()
    {
        Assert.SkipWhen(BundledFiles.Read("wow-optimize/wow_optimize.dll") is null, "Run tools/build-wow-optimize.ps1");
        var mod = ModManifest.Embedded().Mods.Single(m => m.Id == "wow-optimize");
        Assert.All(mod.Files.Where(f => !f.Optional && !f.Config), f => Assert.NotNull(BundledFiles.Read(f.Bundled!)));
        Assert.DoesNotContain(mod.Files, ModInstaller.IsProxy);
    }

    // A bundled mod installs offline and reads as installed by its bundled files' hashes, with no record needed.
    [Fact]
    public async Task InstallsBundledMod()
    {
        var mod = ModManifest.Embedded().Mods.Single(m => m.Id == "login-screens");
        await ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(Game, "Data", "Interface", "GlueXML", "CharacterSelectReorder.lua")));
        Assert.Equal(ModState.Installed, ModInstaller.Detect(mod, Game, "DLLs", null));
    }

    // Project Reforged is detected by its lettered patches; the stock client's numbered ones don't count.
    [Fact]
    public void DetectsReforged()
    {
        var data = Directory.CreateDirectory(Path.Combine(Game, "Data")).FullName;
        File.WriteAllText(Path.Combine(data, "patch-3.MPQ"), "");
        Assert.False(ClientFeatures.HasReforged(Game));
        Assert.True(ClientFeatures.Has(null, Game));
        File.WriteAllText(Path.Combine(data, "patch-I.mpq"), "");
        Assert.True(ClientFeatures.Has(ClientFeatures.Reforged, Game));
    }

    // Cleanup finds old addon folders and replaced settings files (parked ones too), never WowExt.exe.bak or links.
    [Fact]
    public void FindsBackups()
    {
        var addOns = Directory.CreateDirectory(Path.Combine(Game, "Interface", "AddOns")).FullName;
        Directory.CreateDirectory(Path.Combine(addOns, "Foo.bak"));
        File.WriteAllText(Path.Combine(addOns, "Foo.bak", "a.lua"), "12345");
        Directory.CreateDirectory(Path.Combine(addOns, "Bar"));
        File.WriteAllText(Path.Combine(Game, "WowExt.exe.bak"), "original");
        Directory.CreateDirectory(Path.Combine(Game, "WTF"));
        File.WriteAllText(Path.Combine(Game, "WTF", "dxvk.conf.bak"), "old");
        var parked = new ModDefinition("p", "P", "", null, null, null, "1", [], [new ModFile("{dlls}/P.ini", Config: true)]);
        Directory.CreateDirectory(Path.Combine(Game, "DLLs", "_Disabled"));
        File.WriteAllText(Path.Combine(Game, "DLLs", "_Disabled", "P.ini.bak"), "");
        var found = Backups.Find(Game, [DxvkMod(), parked], "DLLs");
        Assert.Equal(3, found.Count);
        Assert.Contains(found, b => b.Path.EndsWith("Foo.bak") && b.Bytes == 5);
        Assert.DoesNotContain(found, b => b.Path.EndsWith("WowExt.exe.bak"));
    }

    // Every bundled preset ships inside the launcher; the no-file one has none, and downloaded ones none until fetched.
    [Fact]
    public void PresetFilesAreEmbedded() =>
        Assert.All(ModManifest.Embedded().Mods.SelectMany(m => m.Files).SelectMany(f => f.Presets ?? []),
            p => Assert.Equal(p.File is not null, ModPresets.Content(p) is not null));

    private ModDefinition DownloadedPresetMod(string zip, string other) =>
        new("lux", "Lux", "", null, null, null, "1", [], [
            new ModFile("{dlls}/Lux.dll", Url: zip, Entry: "Lux.dll"),
            new ModFile("{dlls}/Lux.ini", Url: zip, Entry: "Lux.ini", Config: true, Presets: [
                new ModPreset("default", "Mod Defaults", "", Url: zip, Entry: "Lux.ini"),
                new ModPreset("other", "Other", "", Url: other)])]);

    // Installing writes the first downloaded preset from the mod's own archive and caches it, so it's recognized
    // without fetching again.
    [Fact]
    public async Task InstallCachesFirstDownloadedPreset()
    {
        var zip = MakeZip(("Lux.dll", "dll"), ("Lux.ini", "stock=1"));
        var mod = DownloadedPresetMod(zip, new Uri(Path.Combine(_root, "missing.ini")).AbsoluteUri);
        await ModInstaller.InstallAsync(_github, mod, Game, "DLLs");
        Assert.Equal("stock=1", File.ReadAllText(Path.Combine(Game, "DLLs", "Lux.ini")));
        Assert.Equal("default", ModPresets.Detect(ModPresets.PresetFile(mod)!, Game, "DLLs").Current?.Id);
    }

    // A preset downloaded from a plain URL applies after fetching and then reads as itself; before fetching the
    // file it wrote counts as custom.
    [Fact]
    public async Task FetchesAndAppliesDownloadedPreset()
    {
        var zip = MakeZip(("Lux.dll", "dll"), ("Lux.ini", "stock=1"));
        var otherPath = Path.Combine(_root, "other.ini");
        File.WriteAllText(otherPath, "tuned=1");
        var mod = DownloadedPresetMod(zip, new Uri(otherPath).AbsoluteUri);
        var file = ModPresets.PresetFile(mod)!;
        await ModInstaller.InstallAsync(_github, mod, Game, "DLLs");
        File.WriteAllText(Path.Combine(Game, "DLLs", "Lux.ini"), "tuned=1");
        Assert.True(ModPresets.Detect(file, Game, "DLLs").IsCustom);
        var other = Preset(file, "other");
        await ModPresets.FetchAsync(_github, other);
        Assert.Equal("other", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
        ModPresets.Apply(file, Preset(file, "default"), Game, "DLLs");
        Assert.Equal("stock=1", File.ReadAllText(Path.Combine(Game, "DLLs", "Lux.ini")));
        Assert.Equal("tuned=1", File.ReadAllText(Path.Combine(Game, "DLLs", "Lux.ini.bak")));
    }

    // LuxVelum offers its own settings and Project Reforged's, both downloaded from their sources.
    [Fact]
    public void LuxVelumHasReforgedPreset()
    {
        var file = ModPresets.PresetFile(ModManifest.Embedded().Mods.Single(m => m.Id == "luxvelum"))!;
        Assert.Equal(["default", "reforged"], file.Presets!.Select(p => p.Id));
        Assert.All(file.Presets!, p => Assert.True(p.IsDownloaded));
        Assert.StartsWith(Core.Reforged.ReforgedCatalog.BucketBase, Preset(file, "reforged").Url);
    }

    private static ModDefinition DxvkMod() => ModManifest.Embedded().Mods.Single(m => m.Id == "dxvk");

    // wow-optimize offers Mod Defaults (no settings file, so a fresh install writes none) and Fewer Diagnostics,
    // which turns off only its logging and diagnostic switches.
    [Fact]
    public void WowOptimizeHasDiagnosticsPreset()
    {
        var file = ModPresets.PresetFile(ModManifest.Embedded().Mods.Single(m => m.Id == "wow-optimize"))!;
        Assert.Equal("WTF/wow_opt.ini", file.Path);
        Assert.True(ModPresets.InstallsNothing(file));
        var ini = System.Text.Encoding.UTF8.GetString(ModPresets.Content(Preset(file, "fewer-diagnostics"))!);
        Assert.All(new[] { "SessionLogs=0", "NetDiag=0", "ClientCodeAudit=0", "FlightRecorder=0", "FileAttrCensus=0", "LuaCompileCensus=0" },
            s => Assert.Contains(s, ini));
        Assert.DoesNotContain("=1", ini);
    }

    private static ModPreset Preset(ModFile file, string id) => file.Presets!.Single(p => p.Id == id);

    // With no config file, Mod Defaults is current; a written preset reads as itself.
    [Fact]
    public void DetectsPresets()
    {
        var file = ModPresets.PresetFile(DxvkMod())!;
        Assert.Equal("default", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
        Directory.CreateDirectory(Path.Combine(Game, "WTF"));
        File.WriteAllBytes(Path.Combine(Game, "WTF", "dxvk.conf"), ModPresets.Content(Preset(file, "original-cursor"))!);
        Assert.Equal("original-cursor", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
    }

    // A fresh DXVK install writes no config, so DXVK runs on its own defaults.
    [Fact]
    public async Task FreshInstallWritesNoConfig()
    {
        var mod = DxvkMod() with { Files = [new ModFile("{dlls}/d3d9.dll", Content: "dll"), ModPresets.PresetFile(DxvkMod())!] };
        await ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(Game, "DLLs", "d3d9.dll")));
        Assert.False(File.Exists(Path.Combine(Game, "WTF", "dxvk.conf")));
    }

    // Choosing Mod Defaults saves the player's file as .bak and removes it.
    [Fact]
    public void ModDefaultsRemovesConfig()
    {
        var file = ModPresets.PresetFile(DxvkMod())!;
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Game, "WTF")).FullName, "dxvk.conf");
        File.WriteAllText(path, "mine");
        ModPresets.Apply(file, Preset(file, "default"), Game, "DLLs");
        Assert.False(File.Exists(path));
        Assert.Equal("mine", File.ReadAllText(path + ".bak"));
        Assert.Equal("default", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
    }

    // Line-ending differences still match a preset, and a file matching none reads as custom.
    [Fact]
    public void ToleratesLineEndingsAndSpotsCustom()
    {
        var file = ModPresets.PresetFile(DxvkMod())!;
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Game, "WTF")).FullName, "dxvk.conf");
        File.WriteAllText(path, "d3d9.enlargeHardwareCursor = 1\r\nd3d9.customVendorId = 1002\r\n\r\n");
        Assert.Equal("luxvelum-msaa-original-cursor", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
        File.WriteAllText(path, "d3d9.maxFrameRate = 60\n");
        Assert.True(ModPresets.Detect(file, Game, "DLLs").IsCustom);
    }

    // Applying a preset saves the player's file as .bak first.
    [Fact]
    public void ApplyingKeepsBackup()
    {
        var file = ModPresets.PresetFile(DxvkMod())!;
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Game, "WTF")).FullName, "dxvk.conf");
        File.WriteAllText(path, "mine");
        ModPresets.Apply(file, Preset(file, "luxvelum-msaa"), Game, "DLLs");
        Assert.Equal("mine", File.ReadAllText(path + ".bak"));
        Assert.Equal("luxvelum-msaa", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
    }

    // A disabled mod's config is found, and changed, where it's parked.
    [Fact]
    public void PresetsFollowParkedConfig()
    {
        var file = new ModFile("{dlls}/Test.conf", Config: true, Presets: ModPresets.PresetFile(DxvkMod())!.Presets);
        var parked = Path.Combine(Directory.CreateDirectory(Path.Combine(Game, "DLLs", "_Disabled")).FullName, "Test.conf");
        File.WriteAllBytes(parked, ModPresets.Content(Preset(file, "original-cursor"))!);
        ModPresets.Apply(file, Preset(file, "luxvelum-msaa"), Game, "DLLs");
        Assert.Equal("luxvelum-msaa", ModPresets.Detect(file, Game, "DLLs").Current?.Id);
        Assert.False(File.Exists(Path.Combine(Game, "DLLs", "Test.conf")));
    }

    // No mod in the built-in manifest installs a proxy loader such as version.dll or dinput8.dll.
    [Fact]
    public void ManifestHasNoProxyLoaders() =>
        Assert.DoesNotContain(ModManifest.Embedded().Mods.SelectMany(m => m.Files), ModInstaller.IsProxy);

    // Every downloaded file other than a settings file has a pinned hash, so a changed upstream file is refused.
    [Fact]
    public void DownloadedFilesArePinned() =>
        Assert.All(ModManifest.Embedded().Mods.SelectMany(m => m.Files).Where(f => f.Url is not null && !f.Config),
            f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256 ?? ""));

    // A manifest entry that would install a proxy loader is refused before anything is written.
    [Fact]
    public async Task RefusesProxyLoaders()
    {
        var url = MakeZip(("version.dll", "proxy"));
        var mod = new ModDefinition("p", "P", "", null, null, null, "1", [], [new ModFile("version.dll", url, "version.dll")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(Game, "version.dll")));
    }

    private static ModDefinition TestMod(string url, string? sha = null) => new("test", "Test", "", null, null, null, "1", [],
    [
        new ModFile("{dlls}/Test.dll", url, "Test.dll", sha),
        new ModFile("{dlls}/Test.ini", url, "Test.ini", Config: true),
        new ModFile("WTF/test.conf", Content: "a = 1", Config: true),
    ]);

    // Installing writes entries found at any depth, records hashes, and detects as installed afterwards.
    [Fact]
    public async Task InstallsAndDetects()
    {
        var mod = TestMod(MakeZip(("pkg/bin/Test.dll", "dll"), ("pkg/Test.ini", "ini")));
        Assert.Equal(ModState.NotInstalled, ModInstaller.Detect(mod, Game, "DLLs", null));
        var record = await ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(Game, "DLLs", "Test.dll")));
        Assert.Equal("a = 1", File.ReadAllText(Path.Combine(Game, "WTF", "test.conf")));
        Assert.Equal(Sha("dll"), record.Hashes["{dlls}/Test.dll"]);
        Assert.Equal(ModState.Installed, ModInstaller.Detect(mod, Game, "DLLs", record));
        Assert.Equal(ModState.OtherVersion, ModInstaller.Detect(mod, Game, "DLLs", null));
    }

    // A config file the player already has is never overwritten, and removal leaves it in place.
    [Fact]
    public async Task KeepsConfigFiles()
    {
        var mod = TestMod(MakeZip(("Test.dll", "dll"), ("Test.ini", "new")));
        Directory.CreateDirectory(Path.Combine(Game, "DLLs"));
        File.WriteAllText(Path.Combine(Game, "DLLs", "Test.ini"), "mine");
        await ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Game, "DLLs", "Test.ini")));
        ModInstaller.Uninstall(mod, Game, "DLLs");
        Assert.False(File.Exists(Path.Combine(Game, "DLLs", "Test.dll")));
        Assert.True(File.Exists(Path.Combine(Game, "DLLs", "Test.ini")));
    }

    // Disabling parks a DLL mod's files in _Disabled (config included) and enabling brings them back.
    [Fact]
    public async Task DisablesAndEnables()
    {
        var mod = TestMod(MakeZip(("Test.dll", "dll"), ("Test.ini", "ini"))) with
        {
            Files = [new ModFile("{dlls}/Test.dll", null, Content: "dll"), new ModFile("{dlls}/Test.ini", Content: "ini", Config: true)],
        };
        var record = await ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken);
        ModInstaller.SetEnabled(mod, Game, "DLLs", enabled: false);
        Assert.Equal(ModState.Disabled, ModInstaller.Detect(mod, Game, "DLLs", record));
        Assert.True(File.Exists(Path.Combine(Game, "DLLs", "_Disabled", "Test.ini")));
        ModInstaller.SetEnabled(mod, Game, "DLLs", enabled: true);
        Assert.Equal(ModState.Installed, ModInstaller.Detect(mod, Game, "DLLs", record));
    }

    // A pinned hash that doesn't match stops the install before anything is written.
    [Fact]
    public async Task RefusesWrongHash()
    {
        var mod = TestMod(MakeZip(("Test.dll", "tampered"), ("Test.ini", "")), Sha("dll"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModInstaller.InstallAsync(_github, mod, Game, "DLLs", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(Game, "DLLs")));
    }

    // A required entry missing from the download fails; an optional one is skipped.
    [Fact]
    public async Task HandlesMissingEntries()
    {
        var url = MakeZip(("Other.dll", ""));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModInstaller.InstallAsync(_github, TestMod(url), Game, "DLLs", TestContext.Current.CancellationToken));
        var optional = new ModDefinition("o", "O", "", null, null, null, "1", [], [new ModFile("{dlls}/x.sym", url, "x.sym", Optional: true)]);
        var record = await ModInstaller.InstallAsync(_github, optional, Game, "DLLs", TestContext.Current.CancellationToken);
        Assert.Empty(record.Hashes);
    }

    // The DLL folder setting decides where {dlls} files go.
    [Fact]
    public void ResolvesDllFolder() =>
        Assert.Equal(Path.Combine("G", "Mods", "Native", "a.dll"), ModInstaller.ResolvePath("G", @"Mods\Native", "{dlls}/a.dll"));

    // Launcher release tags parse with or without a leading v.
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.4", "0.4")]
    [InlineData("build-1", null)]
    public void ParsesLauncherTags(string tag, string? expected) =>
        Assert.Equal(expected, SelfUpdate.ParseTag(tag)?.ToString());
}
