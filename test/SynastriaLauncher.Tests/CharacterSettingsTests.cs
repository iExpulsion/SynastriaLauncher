// Covers copying one character's settings to another: finding the characters in WTF, linking AceDB profiles in
// the account's SavedVariables, copying the enabled addons, layout and chat files, merging game options without
// character state, backing up what changes, and refusing to link profiles across accounts.

using System.Text;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Addons;

namespace SynastriaLauncher.Tests;

public sealed class CharacterSettingsTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("launcher-chars-").FullName;

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private static readonly DateTime Now = new(2026, 11, 25, 10, 30, 0);
    private static readonly WtfCharacter Main = new("ONE", "Realm", "Main");
    private static readonly WtfCharacter Alt = new("ONE", "Realm", "Alt");
    private static readonly WtfCharacter Other = new("TWO", "Realm", "Other");

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_game, "WTF", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_game, "WTF", relative));

    private const string Elv =
        "ElvDB = {\r\n"
        + "\t[\"profileKeys\"] = {\r\n"
        + "\t\t[\"Main - Realm\"] = \"Main - Realm\",\r\n"
        + "\t},\r\n"
        + "\t[\"profiles\"] = {\r\n"
        + "\t},\r\n"
        + "}\r\n"
        + "ElvPrivateDB = {\r\n"
        + "\t[\"profileKeys\"] = {\r\n"
        + "\t\t[\"Main - Realm\"] = \"Main - Realm\",\r\n"
        + "\t\t[\"Alt - Realm\"] = \"Alt - Realm\",\r\n"
        + "\t},\r\n"
        + "}\r\n";

    private void MakeCharacters()
    {
        Directory.CreateDirectory(Main.Folder(_game));
        Directory.CreateDirectory(Alt.Folder(_game));
        Directory.CreateDirectory(Other.Folder(_game));
    }

    // Characters are listed from every account and realm, and the account SavedVariables folder isn't a realm.
    [Fact]
    public void ListsCharactersFromWtf()
    {
        MakeCharacters();
        Directory.CreateDirectory(Path.Combine(_game, @"WTF\Account\ONE\SavedVariables"));
        var characters = CharacterSettings.Characters(_game);
        Assert.Equal(["Alt", "Main", "Other"], characters.Select(c => c.Name));
        Assert.Equal("Other (Realm, TWO)", characters[2].Label);
    }

    // A missing key is added and a different profile is replaced, in every table that has the source's key, and the
    // file's CRLF line breaks are kept.
    [Fact]
    public void LinksEveryProfileKeysTable()
    {
        var (text, linked) = CharacterSettings.LinkProfiles(Elv, "Main - Realm", "Alt - Realm");
        Assert.Equal(["ElvDB", "ElvPrivateDB"], linked);
        Assert.Equal(2, text.Split("[\"Alt - Realm\"] = \"Main - Realm\",\r\n").Length - 1);
        Assert.DoesNotContain("\"Alt - Realm\"] = \"Alt - Realm\"", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // A table without the source's key, or one already linked, is left alone.
    [Fact]
    public void LinkLeavesOtherTablesAlone()
    {
        var linkedAlready = Elv.Replace("[\"Alt - Realm\"] = \"Alt - Realm\"", "[\"Alt - Realm\"] = \"Main - Realm\"");
        var (text, linked) = CharacterSettings.LinkProfiles(linkedAlready, "Main - Realm", "Alt - Realm");
        Assert.Equal(["ElvDB"], linked);
        var (unchanged, none) = CharacterSettings.LinkProfiles(Elv, "Nobody - Realm", "Alt - Realm");
        Assert.Empty(none);
        Assert.Equal(Elv, unchanged);
        Assert.NotEqual(linkedAlready, text);
    }

    // Game options take the source's values and add missing ones, but character state (tracked quests, achievements,
    // the saved camera) stays.
    [Fact]
    public void MergesGameOptionsWithoutState()
    {
        var source = "SET autoLootDefault \"1\"\r\nSET trackedQuests \"abc\"\r\nSET trackedAchievements \"v#5&\"\r\n"
            + "SET cameraSavedPitch \"19.0\"\r\nSET cameraDistanceMax \"50\"\r\n";
        var target = "SET autoLootDefault \"0\"\r\nSET trackedQuests \"xyz\"\r\nSET cameraSavedPitch \"18.0\"\r\n";
        var (text, changed) = CharacterSettings.MergeCVars(source, target);
        Assert.Equal("SET autoLootDefault \"1\"\r\nSET trackedQuests \"xyz\"\r\nSET cameraSavedPitch \"18.0\"\r\nSET cameraDistanceMax \"50\"\r\n", text);
        Assert.Equal(["autoLootDefault", "cameraDistanceMax"], changed);
    }

    // A full copy links profiles, copies the files, merges options and backs up everything it changed.
    [Fact]
    public void CopiesEverythingWithBackups()
    {
        MakeCharacters();
        Write(@"Account\ONE\SavedVariables\ElvUI.lua", Elv);
        Write(@"Account\ONE\SavedVariables\ElvUI.lua.bak", Elv);
        Write(@"Account\ONE\Realm\Main\AddOns.txt", "Skada: enabled\r\n");
        Write(@"Account\ONE\Realm\Main\layout-local.txt", "main layout");
        Write(@"Account\ONE\Realm\Alt\layout-local.txt", "alt layout");
        Write(@"Account\ONE\Realm\Main\config-cache.wtf", "SET autoLootDefault \"1\"\r\n");
        Write(@"Account\ONE\Realm\Alt\config-cache.wtf", "SET autoLootDefault \"0\"\r\n");
        var result = CharacterSettings.Copy(_game, Main, Alt, CharacterParts.All, Now);
        Assert.Contains("[\"Alt - Realm\"] = \"Main - Realm\"", Read(@"Account\ONE\SavedVariables\ElvUI.lua"));
        Assert.Equal(Elv, Read(@"Account\ONE\SavedVariables\ElvUI.lua.bak"));
        Assert.Equal("Skada: enabled\r\n", Read(@"Account\ONE\Realm\Alt\AddOns.txt"));
        Assert.Equal("main layout", Read(@"Account\ONE\Realm\Alt\layout-local.txt"));
        Assert.Equal("SET autoLootDefault \"1\"\r\n", Read(@"Account\ONE\Realm\Alt\config-cache.wtf"));
        Assert.Equal(4, result.Changes.Count);
        Assert.NotNull(result.BackupFolder);
        Assert.Equal(Elv, File.ReadAllText(Path.Combine(result.BackupFolder, @"Account\ONE\SavedVariables\ElvUI.lua")));
        Assert.Equal("alt layout", File.ReadAllText(Path.Combine(result.BackupFolder, @"Account\ONE\Realm\Alt\layout-local.txt")));
        Assert.Contains(Backups.Find(_game, [], "DLLs"), b => b.Path == result.BackupFolder);
    }

    // Only the chosen parts change.
    [Fact]
    public void CopiesOnlyChosenParts()
    {
        MakeCharacters();
        Write(@"Account\ONE\SavedVariables\ElvUI.lua", Elv);
        Write(@"Account\ONE\Realm\Main\layout-local.txt", "main layout");
        var result = CharacterSettings.Copy(_game, Main, Alt, CharacterParts.Layout, Now);
        Assert.Equal(Elv, Read(@"Account\ONE\SavedVariables\ElvUI.lua"));
        Assert.Equal("main layout", Read(@"Account\ONE\Realm\Alt\layout-local.txt"));
        Assert.Equal(["Copied layout-local.txt"], result.Changes);
    }

    // Profiles can't be linked across accounts; the copy says so and still copies the files.
    [Fact]
    public void AcrossAccountsSkipsProfiles()
    {
        MakeCharacters();
        Write(@"Account\ONE\SavedVariables\ElvUI.lua", Elv);
        Write(@"Account\ONE\Realm\Main\chat-cache.txt", "chat");
        var result = CharacterSettings.Copy(_game, Main, Other, CharacterParts.All, Now);
        Assert.Equal(Elv, Read(@"Account\ONE\SavedVariables\ElvUI.lua"));
        Assert.Equal("chat", Read(@"Account\TWO\Realm\Other\chat-cache.txt"));
        Assert.Contains(result.Changes, c => c.Contains("different accounts"));
    }

    // Nothing to change means no backup folder.
    [Fact]
    public void NothingToCopyMakesNoBackup()
    {
        MakeCharacters();
        var result = CharacterSettings.Copy(_game, Main, Alt, CharacterParts.All, Now);
        Assert.Empty(result.Changes);
        Assert.Null(result.BackupFolder);
    }

    // Bytes outside ASCII (accented names in saved text) survive a link unchanged.
    [Fact]
    public void KeepsLatin1Bytes()
    {
        MakeCharacters();
        var bytes = Encoding.Latin1.GetBytes(Elv + "Note = \"café\"\r\n");
        var path = Write(@"Account\ONE\SavedVariables\ElvUI.lua", "");
        File.WriteAllBytes(path, bytes);
        CharacterSettings.Copy(_game, Main, Alt, CharacterParts.AddonProfiles, Now);
        Assert.True(File.ReadAllBytes(path).AsSpan().EndsWith(Encoding.Latin1.GetBytes("Note = \"café\"\r\n")));
    }
}
