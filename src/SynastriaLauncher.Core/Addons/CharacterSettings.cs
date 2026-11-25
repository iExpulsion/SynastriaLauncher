using System.Text;
using System.Text.RegularExpressions;

namespace SynastriaLauncher.Core.Addons;

// A character the game has kept settings for: WTF\Account\<account>\<realm>\<name>.
public sealed record WtfCharacter(string Account, string Realm, string Name)
{
    public string Label => $"{Name} ({Realm}, {Account})";

    // How AceDB names the character in its profileKeys.
    public string ProfileKey => $"{Name} - {Realm}";

    public string Folder(string gameFolder) => Path.Combine(SavedSettings.WtfPath(gameFolder), "Account", Account, Realm, Name);

    public bool SameAs(WtfCharacter other) =>
        string.Equals(Account, other.Account, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Realm, other.Realm, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
}

[Flags]
public enum CharacterParts
{
    None = 0,
    AddonProfiles = 1,
    EnabledAddons = 2,
    Layout = 4,
    GameOptions = 8,
    All = AddonProfiles | EnabledAddons | Layout | GameOptions,
}

public sealed record CharacterCopyResult(IReadOnlyList<string> Changes, string? BackupFolder);

// Gives one character another's settings. Addon profiles are linked rather than copied: every AceDB addon in the
// account's SavedVariables points the target at the source's profile, so later changes reach both. That needs both
// characters on one account, since each account has its own files. The rest is copied: the enabled addon list
// (AddOns.txt), frame positions and chat windows (layout-local.txt, chat-cache.txt) and the character's game options
// (config-cache.wtf, leaving out what's character state). Files are backed up before they change.
public static class CharacterSettings
{
    public const string BackupFolderName = "_AltSyncBackups";

    private static readonly string[] LayoutFiles = ["layout-local.txt", "chat-cache.txt"];

    private static readonly Regex StateCVar = new(@"^(trackedQuests|trackedAchievements|questLogCollapseFilter|cameraSaved\w*|lfgSelectedRoles|lfd\w*|currencyTokens\w*|showTokenFrame\w*|lastCharacterIndex)$",
        RegexOptions.IgnoreCase);

    private static readonly Regex TopLevel = new(@"^([A-Za-z_]\w*) = ");
    private static readonly Regex ProfileKeys = new(@"^\t\[""profileKeys""\] = \{");
    private static readonly Regex KeyLine = new(@"^\t\t\[""(.+)""\] = ""(.*)"",");
    private static readonly Regex SetLine = new(@"^SET (\S+) """);

    public static string BackupsPath(string gameFolder) => Path.Combine(SavedSettings.WtfPath(gameFolder), BackupFolderName);

    public static IReadOnlyList<WtfCharacter> Characters(string gameFolder)
    {
        var accounts = Path.Combine(SavedSettings.WtfPath(gameFolder), "Account");
        return Directory.Exists(accounts)
            ? Directory.EnumerateDirectories(accounts)
                .SelectMany(account => Directory.EnumerateDirectories(account)
                    .Where(realm => !string.Equals(Path.GetFileName(realm), "SavedVariables", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(realm => Directory.EnumerateDirectories(realm)
                        .Select(c => new WtfCharacter(Path.GetFileName(account), Path.GetFileName(realm), Path.GetFileName(c)))))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Account, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    public static CharacterCopyResult Copy(string gameFolder, WtfCharacter from, WtfCharacter to, CharacterParts parts, DateTime now)
    {
        var wtf = SavedSettings.WtfPath(gameFolder);
        var backup = Path.Combine(BackupsPath(gameFolder), $"{now:yyyy-MM-dd_HHmmss}_{to.Name}");
        var changes = new List<string>();
        var source = from.Folder(gameFolder);
        var target = to.Folder(gameFolder);
        var sameAccount = string.Equals(from.Account, to.Account, StringComparison.OrdinalIgnoreCase);

        void Save(string path, string text)
        {
            var saved = Path.Combine(backup, Path.GetRelativePath(wtf, path));
            if (File.Exists(path) && !File.Exists(saved))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(path, saved);
            }
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        }

        if (parts.HasFlag(CharacterParts.AddonProfiles) && !sameAccount)
        {
            changes.Add("Addon profiles weren't linked: the characters are on different accounts.");
        }
        else if (parts.HasFlag(CharacterParts.AddonProfiles))
        {
            var folder = Path.Combine(wtf, "Account", from.Account, "SavedVariables");
            IEnumerable<string> files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.lua").Order(StringComparer.OrdinalIgnoreCase) : [];
            foreach (var file in files)
            {
                var (text, linked) = LinkProfiles(Read(file), from.ProfileKey, to.ProfileKey);
                if (linked.Count > 0)
                {
                    Save(file, text);
                    changes.Add($"{Path.GetFileNameWithoutExtension(file)} now uses {from.Name}'s profile");
                }
            }
        }

        var copied = (parts.HasFlag(CharacterParts.EnabledAddons) ? ["AddOns.txt"] : Array.Empty<string>())
            .Concat(parts.HasFlag(CharacterParts.Layout) ? LayoutFiles : []);
        foreach (var name in copied)
        {
            var src = Path.Combine(source, name);
            var dst = Path.Combine(target, name);
            if (File.Exists(src) && (!File.Exists(dst) || !File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst))))
            {
                Save(dst, Read(src));
                changes.Add($"Copied {name}");
            }
        }

        var cvars = "config-cache.wtf";
        if (parts.HasFlag(CharacterParts.GameOptions) && File.Exists(Path.Combine(source, cvars)) && File.Exists(Path.Combine(target, cvars)))
        {
            var (text, changed) = MergeCVars(Read(Path.Combine(source, cvars)), Read(Path.Combine(target, cvars)));
            if (changed.Count > 0)
            {
                Save(Path.Combine(target, cvars), text);
                changes.Add($"Game options from {from.Name}: {changed.Count} changed");
            }
        }

        return new CharacterCopyResult(changes, Directory.Exists(backup) ? backup : null);
    }

    // Points targetKey at sourceKey's profile in every top-level table's profileKeys that holds sourceKey.
    // Returns the new text and the tables that changed.
    public static (string Text, IReadOnlyList<string> Linked) LinkProfiles(string text, string sourceKey, string targetKey)
    {
        var lines = SplitLines(text);
        var output = new StringBuilder(text.Length + 64);
        var linked = new List<string>();
        string? current = null;
        List<string>? block = null;
        foreach (var line in lines)
        {
            current = TopLevel.Match(line) is { Success: true } top ? top.Groups[1].Value : current;
            if (block is not null)
            {
                block.Add(line);
                if (line.StartsWith("\t}", StringComparison.Ordinal))
                {
                    var changed = Relink(block, sourceKey, targetKey);
                    block.ForEach(l => output.Append(l));
                    if (changed && current is not null)
                    {
                        linked.Add(current);
                    }
                    block = null;
                }
            }
            else
            {
                output.Append(line);
                block = ProfileKeys.IsMatch(line) ? new List<string>() : null;
            }
        }
        block?.ForEach(l => output.Append(l));
        return (output.ToString(), linked);
    }

    private static bool Relink(List<string> block, string sourceKey, string targetKey)
    {
        var keys = block.Select((line, i) => (Match: KeyLine.Match(line), Index: i)).Where(k => k.Match.Success)
            .ToDictionary(k => k.Match.Groups[1].Value, k => (k.Index, Profile: k.Match.Groups[2].Value));
        var changed = false;
        if (keys.TryGetValue(sourceKey, out var source))
        {
            var newline = block[0].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var entry = $"\t\t[\"{targetKey}\"] = \"{source.Profile}\",{newline}";
            if (!keys.TryGetValue(targetKey, out var target))
            {
                block.Insert(block.Count - 1, entry);
                changed = true;
            }
            else if (target.Profile != source.Profile)
            {
                block[target.Index] = entry;
                changed = true;
            }
        }
        return changed;
    }

    // The target's config-cache.wtf with the source's value for every CVar that isn't character state. Returns the
    // new text and the CVars that changed or were added.
    public static (string Text, IReadOnlyList<string> Changed) MergeCVars(string sourceText, string targetText)
    {
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(sourceText))
        {
            if (SetLine.Match(line) is { Success: true } m && !StateCVar.IsMatch(m.Groups[1].Value))
            {
                source.TryAdd(m.Groups[1].Value, line);
            }
        }
        var output = new StringBuilder(targetText.Length + 64);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var newline = targetText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var line in SplitLines(targetText))
        {
            var name = SetLine.Match(line) is { Success: true } m ? m.Groups[1].Value : null;
            if (name is not null && source.TryGetValue(name, out var value))
            {
                seen.Add(name);
                if (value.TrimEnd() != line.TrimEnd())
                {
                    changed.Add(name);
                }
                output.Append(value.TrimEnd('\r', '\n')).Append(line.EndsWith('\n') ? newline : "");
            }
            else
            {
                output.Append(line);
            }
        }
        foreach (var (name, line) in source.Where(s => !seen.Contains(s.Key)))
        {
            output.Append(output.Length > 0 && output[^1] != '\n' ? newline : "").Append(line.TrimEnd('\r', '\n')).Append(newline);
            changed.Add(name);
        }
        return (output.ToString(), changed);
    }

    private static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    // Lines with their line breaks kept.
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', start))
        {
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }
        return lines;
    }
}
