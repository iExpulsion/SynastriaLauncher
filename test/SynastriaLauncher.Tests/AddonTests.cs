// Covers the addon side without the network: Git ref parsing, version and status rules, .toc reading, folder
// naming from .toc paths, installing from zips, repo parsing and saved launcher data.

using System.IO.Compression;
using System.Text;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Addons;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Tests;

public sealed class AddonTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-addons-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Pkt(string line) => $"{line.Length + 4:x4}{line}";

    // The ref advertisement yields HEAD, the default branch from the symref capability, and every ref.
    [Fact]
    public void ParsesRefAdvertisement()
    {
        var head = new string('a', 40);
        var tag = new string('b', 40);
        var body = Pkt("# service=git-upload-pack\n") + "0000" +
            Pkt($"{head} HEAD\0multi_ack symref=HEAD:refs/heads/master agent=git\n") +
            Pkt($"{head} refs/heads/master\n") +
            Pkt($"{tag} refs/tags/v1.2\n") + "0000";
        var refs = GitRefs.Parse(Encoding.ASCII.GetBytes(body));
        Assert.Equal(head, refs.HeadSha);
        Assert.Equal("master", refs.DefaultBranch);
        Assert.Equal(tag, refs.Refs["refs/tags/v1.2"]);
    }

    // Versions compare numerically when both parse, ignoring a leading v; otherwise only equal text is current.
    [Theory]
    [InlineData("v1.10", "1.9", true)]
    [InlineData("1.9", "v1.10", false)]
    [InlineData("v9.1", "v9.1", true)]
    [InlineData("beta", "beta2", false)]
    public void ComparesVersions(string local, string remote, bool current) =>
        Assert.Equal(current, AddonStatus.IsCurrent(local, remote));

    private static readonly LocalAddon Installed = new(true, false, "1.0", new DateTime(2026, 11, 1));

    // A recorded release tag decides first, then a recorded commit when there's no tag.
    [Fact]
    public void RecordedVersionDecides()
    {
        var tagged = new InstallRecord("v1.0", "abc", ["A"], DateTime.Now);
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, tagged, "v1.0", "def", null, null));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, tagged, "v1.1", "def", null, null));
        var untagged = tagged with { Tag = null };
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, untagged, null, "abc", null, null));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, untagged, null, "def", null, null));
    }

    // Without a record, a differing .toc version decides, then file dates against the last commit.
    [Fact]
    public void FallsBackToTocThenDates()
    {
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, null, null, "x", "1.1", null));
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, null, null, "x", "1.0", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, null, null, "x", "1.0", new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(AddonState.Unknown, AddonStatus.Evaluate(Installed, null, null, "x", null, null));
    }

    // Without a record, a .toc matching the latest release's is current, even with newer commits on the branch
    // (LuaBoost 1.9.8 showed an update for a FUNDING.yml commit).
    [Fact]
    public void MatchingReleaseTocIsCurrent()
    {
        var newerCommit = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, null, "v1.0", "x", "1.0", newerCommit));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, null, "v1.1", "x", "1.1", null));
    }

    // Following releases, a copy installed from a commit isn't flagged by later commits: the release .toc decides.
    [Fact]
    public void ReleasesIgnoreCommits()
    {
        var fromCommit = new InstallRecord(null, "abc", ["A"], DateTime.Now);
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, fromCommit, "v1.0", "def", "1.0", null));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, fromCommit, "v1.1", "def", "1.1", null));
    }

    // On Latest Commits, a new commit is an update, and a copy installed from a release is behind the head.
    [Fact]
    public void LatestCommitsFollowTheHead()
    {
        var fromCommit = new InstallRecord(null, "abc", ["A"], DateTime.Now);
        var fromRelease = new InstallRecord("v1.0", null, ["A"], DateTime.Now);
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, fromCommit, "v1.0", "abc", null, null, latestCommits: true));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, fromCommit, "v1.0", "def", null, null, latestCommits: true));
        Assert.Equal(AddonState.UpdateAvailable, AddonStatus.Evaluate(Installed, fromRelease, "v1.0", "def", null, null, latestCommits: true));
        Assert.Equal(AddonState.UpToDate, AddonStatus.Evaluate(Installed, fromRelease, "v1.0", "def", null, null));
    }

    // Release downloads in any common archive format count, a .zip wins over a .7z, and other files are skipped.
    [Fact]
    public void PicksTheReleaseArchive()
    {
        Assert.Equal("Foo.zip", Archives.Best(["Foo.7z", "Foo.zip", "SHA256SUMS"], n => n));
        Assert.Equal("Foo.tar.gz", Archives.Best(["Setup.exe", "Foo.tar.gz"], n => n));
        Assert.Equal("LuaBoost.7z", Archives.Best(["LuaBoost.7z"], n => n));
        Assert.Null(Archives.Best(["Foo.lua", "README.md"], n => n));
    }

    private const string SevenZip = "N3q8ryccAAQJqglrmAAAAAAAAAAhAAAAAAAAAMadNMEBABhwcmludCgxKQojIyBWZXJzaW9uOiAxLjAKAAAAgTMHrg/P2W+8D+vqnL82Pf53Qe42TVJY58vO2hM/Kffv8RNgqLZ81+N255GYne8seIF+/IEGpFgwIfuKUMX7U14CxO7ggvBNkVWEkZxQHJZMHwmLV1w6h8n+Pj2kA1e5ZjTSkysLxOK5Xea0kSBc4brG1wFLkgiUABcGHQEJewAHCwEAASMDAQEFXQAQAAAMgOYKAReSptYAAA==";
    private const string TarGz = "H4sICEBeA2sEAGZvby50YXIA7dSxCoMwEAbgm/sUAZd2qM1Vk4OuhT5CdykOQjElxvfvVRC0iwiaJfdBuCTrf3cP586Y6wvsSDMi4jqY1fGOptDaGsLh35ItQRmIoO9C5ZWCRI35c92tB9bnT0aT5B/DNP+783X+7ivY2GL+iLP8+c0dAEpDBInn//FNG454OoBI0WT+fycP7gVbW5z/69/+5/EvSpn/GLJMPWvfNa69Ke4DWQNCCJGIL8RI4lwAEAAA";

    // A .7z and a .tar.gz (made with 7-Zip: Foo-1.0/Foo/Foo.toc and Core.lua) install like a zip.
    [Theory]
    [InlineData(SevenZip)]
    [InlineData(TarGz)]
    public void InstallsOtherArchiveFormats(string base64)
    {
        var archive = Path.Combine(_root, "download");
        File.WriteAllBytes(archive, Convert.FromBase64String(base64));
        var addOns = Path.Combine(_root, "AddOns");
        Assert.Equal(["Foo"], AddonInstaller.InstallFromArchive(archive, addOns, keepBackup: false));
        Assert.Equal("print(1)\n", File.ReadAllText(Path.Combine(addOns, "Foo", "Core.lua")));
        Assert.Equal("1.0", AddonStatus.ReadTocVersion(Path.Combine(addOns, "Foo")));
    }

    // An archive entry climbing out of the extraction folder stops the install before anything is written.
    [Fact]
    public void RefusesEntriesOutsideTheArchive()
    {
        var addOns = Path.Combine(_root, "AddOns");
        Assert.Throws<InvalidDataException>(() =>
            AddonInstaller.InstallFromArchive(MakeZip(("Foo/Foo.toc", ""), ("../../evil.lua", "")), addOns, false));
        Assert.False(Directory.Exists(Path.Combine(addOns, "Foo")));
    }

    // Missing folders read as not installed and linked folders as linked, whatever the versions say.
    [Fact]
    public void MissingAndLinkedComeFirst()
    {
        Assert.Equal(AddonState.NotInstalled, AddonStatus.Evaluate(Installed with { Installed = false }, null, "v1", "x", null, null));
        Assert.Equal(AddonState.Linked, AddonStatus.Evaluate(Installed with { Linked = true }, null, "v1", "x", "2.0", null));
    }

    // The .toc version line is found with loose spacing and any case.
    [Fact]
    public void ReadsTocVersion() =>
        Assert.Equal("2.3.1", AddonStatus.TocVersion(["## Interface: 30300", "  ##version :  2.3.1 ", "## Title: X"]));

    // The .toc title is the display name, with the game's color codes removed.
    [Fact]
    public void ReadsTocTitleWithoutColors()
    {
        Assert.Equal("InstanceGPS: Synastria",
            AddonStatus.TocTitle(["## Interface: 30300", "## Title: |cffff9a2eInstanceGPS|r|cff8a8a8a: Synastria|r"]));
        Assert.Equal("Scoots Craft", AddonStatus.TocTitle(["##title: Scoots Craft"]));
        Assert.Null(AddonStatus.TocTitle(["## Interface: 30300", "## Title:   "]));
    }

    // Folder names come from the shallowest .toc folders outside Libs; a root .toc names its folder.
    [Fact]
    public void FoldersFromTocPaths()
    {
        var (folders, toc) = UserAddons.FoldersFromTocPaths(
            ["src/Foo/Foo.toc", "src/Foo_Options/Foo_Options.toc", "src/Foo/legacy/Old.toc", "src/Foo/Libs/Lib/Lib.toc"]);
        Assert.Equal(["Foo", "Foo_Options"], folders);
        Assert.Equal("src/Foo/Foo.toc", toc);
        Assert.Equal(["Gear Mover"], UserAddons.FoldersFromTocPaths(["Gear Mover.toc", "Gear Mover-Classic.toc"]).Folders);
    }

    // Repos parse from owner/repo or a GitHub link; other text is refused.
    [Theory]
    [InlineData("SynScoots/ScootsVendor", "SynScoots/ScootsVendor")]
    [InlineData("https://github.com/RosemyneH/OmniInv-Syn.git", "RosemyneH/OmniInv-Syn")]
    [InlineData("https://github.com/a/b/", "a/b")]
    [InlineData("not a repo", null)]
    public void ParsesRepos(string text, string? expected) => Assert.Equal(expected, UserAddons.ParseRepo(text));

    private string MakeZip(params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid()}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    // A source zip's wrapper folder is unwrapped, sibling addons install side by side, Libs and deeper .tocs don't count.
    [Fact]
    public void InstallsAddonFoldersFromZip()
    {
        var zip = MakeZip(
            ("repo-abc/src/Foo/Foo.toc", "## Version: 1"),
            ("repo-abc/src/Foo/Libs/Lib/Lib.toc", ""),
            ("repo-abc/src/Foo/legacy/Old.toc", ""),
            ("repo-abc/src/Foo_Options/Foo_Options.toc", ""));
        var addOns = Path.Combine(_root, "AddOns");
        Assert.Equal(["Foo", "Foo_Options"], AddonInstaller.InstallFromArchive(zip, addOns, keepBackup: false).Order());
        Assert.True(File.Exists(Path.Combine(addOns, "Foo", "Libs", "Lib", "Lib.toc")));
    }

    // A .toc at the archive root names the folder after the shortest .toc there.
    [Fact]
    public void RootTocNamesFolder()
    {
        var zip = MakeZip(("Gear-Mover-main/Gear Mover.toc", ""), ("Gear-Mover-main/Gear Mover-Classic.toc", ""), ("Gear-Mover-main/Core.lua", ""));
        var addOns = Path.Combine(_root, "AddOns");
        Assert.Equal(["Gear Mover"], AddonInstaller.InstallFromArchive(zip, addOns, keepBackup: false));
        Assert.True(File.Exists(Path.Combine(addOns, "Gear Mover", "Core.lua")));
    }

    // Updating replaces the old folder, keeping it as .bak only when asked.
    [Fact]
    public void ReplacesWithOptionalBackup()
    {
        var addOns = Path.Combine(_root, "AddOns");
        Directory.CreateDirectory(Path.Combine(addOns, "Foo"));
        File.WriteAllText(Path.Combine(addOns, "Foo", "old.lua"), "");
        AddonInstaller.InstallFromArchive(MakeZip(("Foo/Foo.toc", "")), addOns, keepBackup: true);
        Assert.False(File.Exists(Path.Combine(addOns, "Foo", "old.lua")));
        Assert.True(File.Exists(Path.Combine(addOns, "Foo.bak", "old.lua")));
        AddonInstaller.InstallFromArchive(MakeZip(("Foo/Foo.toc", "")), addOns, keepBackup: false);
        Assert.True(File.Exists(Path.Combine(addOns, "Foo", "Foo.toc")));
    }

    // A zip without any .toc isn't an addon and installs nothing.
    [Fact]
    public void RefusesZipWithoutToc()
    {
        var addOns = Path.Combine(_root, "AddOns");
        Assert.Throws<InvalidDataException>(() => AddonInstaller.InstallFromArchive(MakeZip(("readme.txt", "")), addOns, false));
        Assert.False(Directory.Exists(addOns));
    }

    // Files edited, added or deleted after install are reported; untouched installs and old records report nothing.
    [Fact]
    public void SpotsLocalEdits()
    {
        var addOns = Path.Combine(_root, "AddOns");
        var folders = AddonInstaller.InstallFromArchive(MakeZip(("Foo/Foo.toc", ""), ("Foo/Core.lua", "a"), ("Foo/Old.lua", "")), addOns, false);
        var record = new InstallRecord("v1", "abc", folders, DateTime.Now, InstalledFiles.Stamp(addOns, folders));
        Assert.Empty(InstalledFiles.Changed(addOns, record));
        File.WriteAllText(Path.Combine(addOns, "Foo", "Core.lua"), "my fix");
        File.WriteAllText(Path.Combine(addOns, "Foo", "Mine.lua"), "");
        File.Delete(Path.Combine(addOns, "Foo", "Old.lua"));
        Assert.Equal(["Core.lua", "Mine.lua", "Old.lua"], InstalledFiles.Changed(addOns, record).Select(Path.GetFileName).Order());
        Assert.Empty(InstalledFiles.Changed(addOns, record with { Files = null }));
    }

    // Launcher data survives a save and load.
    [Fact]
    public void LauncherDataRoundTrips()
    {
        var path = Path.Combine(_root, "launcher.json");
        var data = new LauncherData { GameFolder = "D:\\Game", KeepAddonBackups = true };
        data.UserAddons.Add(new AddonSource("a/b", "B", ["B"], "B/B.toc", false, "Does things."));
        data.Installed["a/b"] = new InstallRecord("v1", "abc", ["B"], new DateTime(2026, 11, 17));
        data.Save(path);
        var loaded = LauncherData.Load(path);
        Assert.Equal("D:\\Game", loaded.GameFolder);
        Assert.True(loaded.KeepAddonBackups);
        Assert.Equal(["B"], loaded.UserAddons.Single().Folders);
        Assert.Equal("Does things.", loaded.UserAddons.Single().Description);
        Assert.Equal("v1", loaded.Installed["a/b"].Tag);
    }

    // The built-in addons.json passes the checks a published list must pass, has a library, and has both browse sections.
    [Fact]
    public void BuiltInAddonListIsValid()
    {
        var catalog = AddonCatalog.Embedded();
        Assert.Empty(catalog.Problems());
        Assert.Contains(catalog.Sources, a => a.Library);
        Assert.Contains(catalog.Sources, a => a.Wotlk);
        Assert.Contains(catalog.Sources, a => !a.Wotlk);
        Assert.All(catalog.Sources, a => Assert.True(a.Curated));
    }

    private static AddonCatalog Catalog(int revision, string? minLauncher, params AddonEntry[] addons) => new(revision, addons, minLauncher);

    private static readonly AddonEntry Good = new("a/Good", "Good", ["Good"], "Good/Good.toc", "Fine.");

    // A published list can't name a URL, a folder outside AddOns, a .toc outside the repo or an unknown requirement.
    [Fact]
    public void UnsafeAddonEntriesAreReported()
    {
        AddonEntry[] bad =
        [
            Good with { Repo = "https://evil.example/x/y" },
            Good with { Folders = ["..\\..\\Windows"] },
            Good with { Folders = ["Interface/AddOns"] },
            Good with { Toc = "../../secret.toc" },
            Good with { Requires = ["nobody/missing"] },
            Good with { Description = " " },
        ];
        Assert.All(bad, entry => Assert.NotEmpty(Catalog(2, null, entry).Problems()));
        Assert.NotEmpty(Catalog(2, null, Good, Good).Problems());
        Assert.Empty(Catalog(2, null, Good).Problems());
        Assert.NotEmpty(Catalog(2, null, Good with { Folders = ["..."] }).Problems());
        Assert.NotEmpty(Catalog(2, null, Good with { Folders = ["Good."] }).Problems());
    }

    // Two addons can't claim the same folder (in any letter case), since installing one would overwrite the other.
    [Fact]
    public void SharedFoldersAreReported()
    {
        var a = Good with { Repo = "x/A", Folders = ["Shared", "OnlyA"] };
        var b = Good with { Repo = "x/B", Folders = ["shared"] };
        Assert.NotEmpty(Catalog(2, null, a, b).Problems());
        Assert.Empty(Catalog(2, null, a, b with { Folders = ["OnlyB"] }).Problems());
    }

    // Addons that require each other (directly or through a third) are refused, since installing one would never end.
    [Fact]
    public void RequirementCyclesAreReported()
    {
        var a = Good with { Repo = "x/A", Folders = ["A"], Requires = ["x/B"] };
        var b = Good with { Repo = "x/B", Folders = ["B"], Requires = ["x/C"] };
        var c = Good with { Repo = "x/C", Folders = ["C"], Requires = ["x/A"] };
        Assert.NotEmpty(Catalog(2, null, a, b, c).Problems());
        Assert.Empty(Catalog(2, null, a, b with { Requires = null }).Problems());
    }

    // A published list missing a field (JSON leaves it null) counts as invalid instead of crashing the launcher.
    [Fact]
    public void MalformedPublishedListFallsBack()
    {
        var builtIn = Catalog(1, null, Good);
        var broken = AddonCatalog.Parse("""{ "revision": 9, "addons": [ { "repo": "a/b", "name": "B", "description": "x" } ] }""");
        Assert.Same(builtIn, LiveList.Pick(builtIn, broken, new Version(1, 0)));
    }

    // A zip whose .toc names no usable folder (".toc" alone at the root) is refused, leaving AddOns untouched.
    [Fact]
    public void ZipWithoutUsableFolderNameIsRefused()
    {
        var addOns = Directory.CreateDirectory(Path.Combine(_root, "AddOns")).FullName;
        File.WriteAllText(Path.Combine(addOns, "Keep.txt"), "mine");
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(".toc").Open());
            writer.Write("## Title: Bad");
        }
        Assert.Throws<InvalidDataException>(() => AddonInstaller.InstallFromArchive(zip, addOns, keepBackup: false));
        Assert.True(File.Exists(Path.Combine(addOns, "Keep.txt")));
        Assert.Throws<IOException>(() => AddonInstaller.Uninstall(addOns, ["..."]));
        Assert.True(File.Exists(Path.Combine(addOns, "Keep.txt")));
    }

    // The published list wins only when it's newer, valid and meant for this launcher version.
    [Fact]
    public void PublishedListNeedsNewerRevisionAndLauncher()
    {
        var builtIn = Catalog(5, null, Good);
        var launcher = new Version(0, 2, 0);
        Assert.Same(builtIn, LiveList.Pick(builtIn, null, launcher));
        Assert.Same(builtIn, LiveList.Pick(builtIn, Catalog(5, null, Good), launcher));
        Assert.Same(builtIn, LiveList.Pick(builtIn, Catalog(6, "0.3.0", Good), launcher));
        Assert.Same(builtIn, LiveList.Pick(builtIn, Catalog(6, "not a version", Good), launcher));
        Assert.Same(builtIn, LiveList.Pick(builtIn, Catalog(6, null, Good with { Folders = [".."] }), launcher));
        var newer = Catalog(6, "0.2.0", Good);
        Assert.Same(newer, LiveList.Pick(builtIn, newer, launcher));
    }

    // An installed addon that left the curated list stays tracked under its saved name; the player's own stay too.
    [Fact]
    public void DroppedAddonsStayTracked()
    {
        var data = new LauncherData();
        data.Installed["gone/Old"] = new InstallRecord("v1", null, ["OldFolder"], DateTime.Now, Name: "Old Addon");
        data.Installed["a/Good"] = new InstallRecord("v1", null, ["Good"], DateTime.Now);
        data.UserAddons.Add(new AddonSource("me/Mine", "Mine", ["Mine"], null, false));
        var sources = data.AddonSources(Catalog(1, null, Good).Sources).ToList();
        Assert.Equal(["a/Good", "me/Mine", "gone/Old"], sources.Select(s => s.Repo));
        Assert.Equal("Old Addon", sources[2].Name);
        Assert.Equal(["OldFolder"], sources[2].Folders);
    }

    private static readonly AddonSource Lib = new("x/Lib", "Lib", ["Lib"], null, true, Library: true);
    private static readonly AddonSource User = new("x/User", "User", ["User"], null, true, Requires: ["x/Lib"]);

    // Only requirements that aren't installed are brought along.
    [Fact]
    public void MissingListsUninstalledRequirements()
    {
        Assert.Equal(["x/Lib"], AddonDependencies.Missing(User, _ => AddonState.NotInstalled));
        Assert.Empty(AddonDependencies.Missing(User, _ => AddonState.UpToDate));
        Assert.Empty(AddonDependencies.Missing(Lib, _ => AddonState.NotInstalled));
    }

    // A library is folded away while there's nothing to do with it, and shows when it needs a look or nothing needs it.
    [Theory]
    [InlineData(AddonState.NotInstalled, AddonState.NotInstalled, false, true)]
    [InlineData(AddonState.UpToDate, AddonState.UpToDate, false, true)]
    [InlineData(AddonState.UpdateAvailable, AddonState.UpToDate, false, true)]
    [InlineData(AddonState.UpdateAvailable, AddonState.UpToDate, true, false)]
    [InlineData(AddonState.UpToDate, AddonState.NotInstalled, false, false)]
    [InlineData(AddonState.Failed, AddonState.UpToDate, false, false)]
    [InlineData(AddonState.Linked, AddonState.UpToDate, false, false)]
    public void LibrariesFoldWhileQuiet(AddonState library, AddonState user, bool needsUpdate, bool folded)
    {
        (AddonSource, AddonState?)[] all = [(Lib, library), (User, user)];
        Assert.Equal(folded, AddonDependencies.IsFolded(Lib, library, needsUpdate, all));
        Assert.False(AddonDependencies.IsFolded(User, user, false, all));
    }
}
