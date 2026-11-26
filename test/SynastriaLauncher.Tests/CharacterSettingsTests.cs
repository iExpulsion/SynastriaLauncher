// Covers copying one character's settings to others: finding the characters in WTF, linking AceDB profiles on the
// source's account, copying profiles (namespaces included) into other accounts' files, copying the enabled addons,
// layout and chat files, merging game options without character state, and backing up what changes once per copy.

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
    private static readonly WtfCharacter Third = new("ONE", "Realm", "Third");
    private static readonly WtfCharacter Other = new("TWO", "Realm", "Other");

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_game, "WTF", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_game, "WTF", relative));

    private static string Crlf(params string[] lines) => string.Concat(lines.Select(l => l + "\r\n"));

    private static readonly string Elv = Crlf(
        "",
        "ElvDB = {",
        "\t[\"profileKeys\"] = {",
        "\t\t[\"Main - Realm\"] = \"Main - Realm\",",
        "\t},",
        "\t[\"profiles\"] = {",
        "\t\t[\"Main - Realm\"] = {",
        "\t\t\t[\"scale\"] = 0.7,",
        "\t\t},",
        "\t},",
        "}",
        "ElvPrivateDB = {",
        "\t[\"profileKeys\"] = {",
        "\t\t[\"Main - Realm\"] = \"Main - Realm\",",
        "\t\t[\"Alt - Realm\"] = \"Alt - Realm\",",
        "\t},",
        "}");

    private static readonly string Skada = Crlf(
        "",
        "SkadaDB = {",
        "\t[\"namespaces\"] = {",
        "\t\t[\"Threat\"] = {",
        "\t\t\t[\"profiles\"] = {",
        "\t\t\t\t[\"Default\"] = {",
        "\t\t\t\t\t[\"warn\"] = true,",
        "\t\t\t\t},",
        "\t\t\t},",
        "\t\t},",
        "\t},",
        "\t[\"profileKeys\"] = {",
        "\t\t[\"Main - Realm\"] = \"Default\",",
        "\t},",
        "\t[\"profiles\"] = {",
        "\t\t[\"Default\"] = {",
        "\t\t\t[\"bars\"] = 10,",
        "\t\t},",
        "\t},",
        "}");

    private void MakeCharacters()
    {
        foreach (var c in new[] { Main, Alt, Third, Other })
        {
            Directory.CreateDirectory(c.Folder(_game));
        }
    }

    // Characters are listed from every account and realm, by account then name, and the account's SavedVariables
    // folder isn't a realm.
    [Fact]
    public void ListsCharactersFromWtf()
    {
        MakeCharacters();
        Directory.CreateDirectory(Path.Combine(_game, @"WTF\Account\ONE\SavedVariables"));
        var characters = CharacterSettings.Characters(_game);
        Assert.Equal(["Alt", "Main", "Third", "Other"], characters.Select(c => c.Name));
        Assert.Equal("Other (Realm, TWO)", characters[3].Label);
    }

    // In the same file, a missing key is added and a different profile replaced in every table that knows the source,
    // nothing else changes, and CRLF line breaks are kept.
    [Fact]
    public void LinksEveryProfileKeysTable()
    {
        var (text, changed) = CharacterSettings.ShareProfiles(Elv, Elv, "Main - Realm", "Alt - Realm", sameFile: true);
        Assert.True(changed);
        Assert.Equal(2, text.Split("[\"Alt - Realm\"] = \"Main - Realm\",\r\n").Length - 1);
        Assert.DoesNotContain("\"Alt - Realm\"] = \"Alt - Realm\"", text);
        Assert.Equal(Elv.Replace("[\"Alt - Realm\"] = \"Alt - Realm\"", "[\"Alt - Realm\"] = \"Main - Realm\"")
            .Replace("\t\t[\"Main - Realm\"] = \"Main - Realm\",\r\n\t},\r\n\t[\"profiles\"]",
                "\t\t[\"Main - Realm\"] = \"Main - Realm\",\r\n\t\t[\"Alt - Realm\"] = \"Main - Realm\",\r\n\t},\r\n\t[\"profiles\"]"), text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // A file where the target already uses the source's profile, or that doesn't know the source, is unchanged.
    [Fact]
    public void LinkLeavesLinkedAndUnknownAlone()
    {
        var (linked, _) = CharacterSettings.ShareProfiles(Elv, Elv, "Main - Realm", "Alt - Realm", sameFile: true);
        Assert.False(CharacterSettings.ShareProfiles(linked, linked, "Main - Realm", "Alt - Realm", sameFile: true).Changed);
        Assert.False(CharacterSettings.ShareProfiles(Elv, Elv, "Nobody - Realm", "Alt - Realm", sameFile: true).Changed);
    }

    // Into another account's file, the profile table is copied over the old one of that name, the namespace's
    // profile comes along, and the target's key points at it; the other account's own data stays.
    [Fact]
    public void CopiesProfileIntoAnotherFile()
    {
        var target = Crlf(
            "",
            "SkadaDB = {",
            "\t[\"profileKeys\"] = {",
            "\t\t[\"Other - Realm\"] = \"Default\",",
            "\t},",
            "\t[\"profiles\"] = {",
            "\t\t[\"Default\"] = {",
            "\t\t\t[\"bars\"] = 3,",
            "\t\t\t[\"old\"] = true,",
            "\t\t},",
            "\t},",
            "\t[\"total\"] = 5,",
            "}");
        var (text, changed) = CharacterSettings.ShareProfiles(Skada, target, "Main - Realm", "Other - Realm", sameFile: false);
        Assert.True(changed);
        Assert.Contains("\t\t\t[\"bars\"] = 10,\r\n", text);
        Assert.DoesNotContain("old", text);
        Assert.Contains("\t\t\t\t\t[\"warn\"] = true,\r\n", text);
        Assert.Contains("\t[\"total\"] = 5,\r\n", text);
        Assert.False(CharacterSettings.ShareProfiles(Skada, text, "Main - Realm", "Other - Realm", sameFile: false).Changed);
    }

    // An account that has no file for the addon yet gets one holding just the shared profile.
    [Fact]
    public void CreatesMissingFile()
    {
        var (text, changed) = CharacterSettings.ShareProfiles(Elv, null, "Main - Realm", "Other - Realm", sameFile: false);
        Assert.True(changed);
        Assert.Contains("ElvDB = {\r\n\t[\"profileKeys\"] = {\r\n\t\t[\"Other - Realm\"] = \"Main - Realm\",\r\n\t},\r\n", text);
        Assert.Contains("\t\t\t[\"scale\"] = 0.7,\r\n", text);
        Assert.Contains("ElvPrivateDB = {\r\n", text);
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

    // A copy to several characters on both accounts links on the source's account, copies into the other, copies the
    // files and options to each, and keeps each original once in a single backup folder.
    [Fact]
    public void CopiesToManyCharactersWithOneBackup()
    {
        MakeCharacters();
        Write(@"Account\ONE\SavedVariables\ElvUI.lua", Elv);
        Write(@"Account\ONE\SavedVariables\ElvUI.lua.bak", Elv);
        Write(@"Account\ONE\Realm\Main\AddOns.txt", "Skada: enabled\r\n");
        Write(@"Account\ONE\Realm\Main\layout-local.txt", "main layout");
        Write(@"Account\ONE\Realm\Alt\layout-local.txt", "alt layout");
        Write(@"Account\ONE\Realm\Main\config-cache.wtf", "SET autoLootDefault \"1\"\r\n");
        Write(@"Account\ONE\Realm\Alt\config-cache.wtf", "SET autoLootDefault \"0\"\r\n");
        var result = CharacterSettings.Copy(_game, Main, [Alt, Third, Other, Main], CharacterParts.All, Now);
        var elv = Read(@"Account\ONE\SavedVariables\ElvUI.lua");
        Assert.Contains("[\"Alt - Realm\"] = \"Main - Realm\"", elv);
        Assert.Contains("[\"Third - Realm\"] = \"Main - Realm\"", elv);
        Assert.Contains("[\"Other - Realm\"] = \"Main - Realm\"", Read(@"Account\TWO\SavedVariables\ElvUI.lua"));
        Assert.Equal(Elv, Read(@"Account\ONE\SavedVariables\ElvUI.lua.bak"));
        Assert.Equal("Skada: enabled\r\n", Read(@"Account\TWO\Realm\Other\AddOns.txt"));
        Assert.Equal("main layout", Read(@"Account\ONE\Realm\Third\layout-local.txt"));
        Assert.Equal("SET autoLootDefault \"1\"\r\n", Read(@"Account\ONE\Realm\Alt\config-cache.wtf"));
        Assert.Contains("Alt: 1 game option", result.Changes);
        Assert.Contains("Other: copied Main's profile in ElvUI", result.Changes);
        Assert.Contains("Third: linked to Main's profile in ElvUI", result.Changes);
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
        var result = CharacterSettings.Copy(_game, Main, [Alt], CharacterParts.Layout, Now);
        Assert.Equal(Elv, Read(@"Account\ONE\SavedVariables\ElvUI.lua"));
        Assert.Equal("main layout", Read(@"Account\ONE\Realm\Alt\layout-local.txt"));
        Assert.Equal(["Alt: copied layout-local.txt"], result.Changes);
    }

    // Nothing to change means no backup folder.
    [Fact]
    public void NothingToCopyMakesNoBackup()
    {
        MakeCharacters();
        var result = CharacterSettings.Copy(_game, Main, [Alt, Other], CharacterParts.All, Now);
        Assert.Empty(result.Changes);
        Assert.Null(result.BackupFolder);
    }

    private string Chat(WtfCharacter c, string text, DateTime savedUtc)
    {
        var path = Write($@"Account\{c.Account}\{c.Realm}\{c.Name}\chat-cache.txt", text);
        File.SetLastWriteTimeUtc(path, savedUtc);
        return path;
    }

    // Shared chat windows: the most recently saved chat-cache.txt goes to the rest of the group (across accounts too,
    // and to a character with none yet), keeping its save time; the replaced copies are backed up.
    [Fact]
    public void SharedChatTakesTheNewest()
    {
        MakeCharacters();
        var saved = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        Chat(Main, "main chat", saved.AddHours(-2));
        var alt = Chat(Alt, "alt chat", saved);
        var result = CharacterSettings.SyncChat(_game, [Main, Alt, Other]);
        Assert.Equal(Alt, result.From);
        Assert.Equal([Main, Other], result.Updated);
        Assert.Equal("alt chat", Read(@"Account\ONE\Realm\Main\chat-cache.txt"));
        Assert.Equal("alt chat", Read(@"Account\TWO\Realm\Other\chat-cache.txt"));
        Assert.Equal(saved, File.GetLastWriteTimeUtc(Path.Combine(Main.Folder(_game), "chat-cache.txt")));
        Assert.Equal(saved, File.GetLastWriteTimeUtc(alt));
        Assert.Equal("main chat", Read(@"_AltSyncBackups\Shared Chat Windows\ONE\Realm\Main\chat-cache.txt"));
    }

    // A group already the same changes nothing, and a character outside the group is left alone.
    [Fact]
    public void SharedChatLeavesMatchingAndOthersAlone()
    {
        MakeCharacters();
        var saved = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        Chat(Main, "same", saved);
        Chat(Alt, "same", saved.AddHours(-1));
        Chat(Third, "third's own", saved.AddHours(1));
        var result = CharacterSettings.SyncChat(_game, [Main, Alt]);
        Assert.Empty(result.Updated);
        Assert.Equal("third's own", Read(@"Account\ONE\Realm\Third\chat-cache.txt"));
        Assert.False(Directory.Exists(Path.Combine(_game, "WTF", "_AltSyncBackups")));
    }

    // Bytes outside ASCII (accented names in saved text) survive a link unchanged.
    [Fact]
    public void KeepsLatin1Bytes()
    {
        MakeCharacters();
        var bytes = Encoding.Latin1.GetBytes(Elv + "Note = \"café\"\r\n");
        var path = Write(@"Account\ONE\SavedVariables\ElvUI.lua", "");
        File.WriteAllBytes(path, bytes);
        CharacterSettings.Copy(_game, Main, [Alt], CharacterParts.AddonProfiles, Now);
        Assert.True(File.ReadAllBytes(path).AsSpan().EndsWith(Encoding.Latin1.GetBytes("Note = \"café\"\r\n")));
    }
}
