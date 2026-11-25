// Covers addons' saved settings in WTF: finding each addon's files on every account and character, spotting the
// ones whose addon is gone, moving them aside with their WTF path kept, and listing the moved ones as backups.

using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Addons;

namespace SynastriaLauncher.Tests;

public sealed class SavedSettingsTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("launcher-settings-").FullName;

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private static readonly DateTime Today = new(2026, 11, 25);

    private string Write(string relative, string text = "Settings = {}")
    {
        var path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void Install(params string[] folders)
    {
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(GameFolder.AddOnsPath(_game), folder));
        }
    }

    // Account and character files are found, .bak copies count as the addon's, and other files are skipped.
    [Fact]
    public void FindsAccountAndCharacterFiles()
    {
        Write(@"WTF\Account\ONE\SavedVariables\Skada.lua");
        Write(@"WTF\Account\ONE\SavedVariables\Skada.lua.bak");
        Write(@"WTF\Account\ONE\Realm\Hero\SavedVariables\Skada.lua");
        Write(@"WTF\Account\ONE\SavedVariables.lua");
        Write(@"WTF\Account\ONE\Realm\Hero\layout-local.txt");
        var all = SavedSettings.All(_game);
        Assert.Equal(3, all.Count);
        Assert.All(all, f => Assert.Equal("Skada", f.Addon));
        Assert.Contains(all, f => f.Character == "Hero" && f.Account == "ONE");
        Assert.Equal(2, all.Count(f => f.Character is null));
    }

    // Only files of addons missing from AddOns are left over; the game's own Blizzard_ addons never are.
    [Fact]
    public void LeftoverSkipsInstalledAndGameAddons()
    {
        Install("Skada");
        Write(@"WTF\Account\ONE\SavedVariables\Skada.lua");
        Write(@"WTF\Account\ONE\SavedVariables\Blizzard_CombatLog.lua");
        Write(@"WTF\Account\ONE\SavedVariables\Questie.lua");
        Write(@"WTF\Account\TWO\Realm\Alt\SavedVariables\Questie.lua");
        var leftover = SavedSettings.Leftover(_game);
        Assert.Equal(2, leftover.Count);
        Assert.All(leftover, f => Assert.Equal("Questie", f.Addon));
        Assert.Equal(["Questie"], SavedSettings.Addons(leftover));
    }

    // An addon's files are found by folder name with any capitals.
    [Fact]
    public void OfMatchesFoldersIgnoringCase()
    {
        Write(@"WTF\Account\ONE\SavedVariables\Soulstone.lua");
        Write(@"WTF\Account\ONE\SavedVariables\Skada.lua");
        var files = SavedSettings.Of(_game, ["soulstone"]);
        Assert.Single(files);
        Assert.Equal("Soulstone", files[0].Addon);
    }

    // Moving aside keeps each file's path inside WTF under the dated folder, so it can be put back.
    [Fact]
    public void MoveAsideKeepsWtfPaths()
    {
        var account = Write(@"WTF\Account\ONE\SavedVariables\Questie.lua", "account");
        var character = Write(@"WTF\Account\ONE\Realm\Hero\SavedVariables\Questie.lua", "character");
        var target = SavedSettings.MoveAside(_game, SavedSettings.Leftover(_game), Today);
        Assert.False(File.Exists(account));
        Assert.False(File.Exists(character));
        Assert.Equal("account", File.ReadAllText(Path.Combine(target, @"Account\ONE\SavedVariables\Questie.lua")));
        Assert.Equal("character", File.ReadAllText(Path.Combine(target, @"Account\ONE\Realm\Hero\SavedVariables\Questie.lua")));
        Assert.Empty(SavedSettings.All(_game));
    }

    // A file moved aside twice in one day keeps the first copy and puts the newer one beside it.
    [Fact]
    public void MoveAsideTwiceKeepsBoth()
    {
        Write(@"WTF\Account\ONE\SavedVariables\Questie.lua.bak", "first");
        var target = SavedSettings.MoveAside(_game, SavedSettings.Leftover(_game), Today);
        Write(@"WTF\Account\ONE\SavedVariables\Questie.lua.bak", "second");
        SavedSettings.MoveAside(_game, SavedSettings.Leftover(_game), Today);
        var folder = Path.Combine(target, @"Account\ONE\SavedVariables");
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "Questie.lua.bak")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(folder, "Questie (2).lua.bak")));
    }

    // Moved-aside settings show up in Backups, one entry per day, so Clean Up can recycle them.
    [Fact]
    public void MovedSettingsAreBackups()
    {
        Write(@"WTF\Account\ONE\SavedVariables\Questie.lua", "12345");
        SavedSettings.MoveAside(_game, SavedSettings.Leftover(_game), Today);
        var found = Backups.Find(_game, [], "DLLs");
        Assert.Contains(found, b => b.Label == "Saved settings of removed addons, moved aside 2026-11-25" && b.Bytes == 5);
    }

    // A game folder without WTF has no saved settings.
    [Fact]
    public void NoWtfFindsNothing()
    {
        Assert.Empty(SavedSettings.All(_game));
        Assert.Empty(SavedSettings.Leftover(_game));
    }
}
