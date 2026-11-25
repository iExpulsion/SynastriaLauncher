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

    public bool SameAccount(WtfCharacter other) => string.Equals(Account, other.Account, StringComparison.OrdinalIgnoreCase);

    public bool SameAs(WtfCharacter other) =>
        SameAccount(other)
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

// Gives characters another's settings. Addon profiles: every AceDB addon points the target at the source's profile.
// On the source's account that's a link, so later changes reach both; another account has its own files, so the
// profile is copied into them (replacing one of the same name) and later changes need another copy. The rest is
// copied: the enabled addon list (AddOns.txt), frame positions and chat windows (layout-local.txt, chat-cache.txt)
// and the character's game options (config-cache.wtf, leaving out what's character state). Every file is backed up
// once per copy before it changes.
public static class CharacterSettings
{
    public const string BackupFolderName = "_AltSyncBackups";

    private static readonly string[] LayoutFiles = ["layout-local.txt", "chat-cache.txt"];

    private static readonly Regex StateCVar = new(@"^(trackedQuests|trackedAchievements|questLogCollapseFilter|cameraSaved\w*|lfgSelectedRoles|lfd\w*|currencyTokens\w*|showTokenFrame\w*|lastCharacterIndex)$",
        RegexOptions.IgnoreCase);

    private static readonly Regex TopLevelTable = new(@"^([A-Za-z_]\w*) = \{\s*$");
    private static readonly Regex KeyLine = new(@"^\t\t\[""(.+)""\] = ""(.*)"",\s*$");
    private static readonly Regex TableKey = new(@"^\t*\[""(.+)""\] = \{\s*$");
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
                .OrderBy(c => c.Account, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    public static CharacterCopyResult Copy(string gameFolder, WtfCharacter from, IReadOnlyList<WtfCharacter> targets,
        CharacterParts parts, DateTime now)
    {
        var wtf = SavedSettings.WtfPath(gameFolder);
        var backup = Path.Combine(BackupsPath(gameFolder), $"{now:yyyy-MM-dd_HHmmss}_from_{from.Name}");
        var changes = new List<string>();
        var others = targets.Where(t => !t.SameAs(from)).ToList();

        void Save(string path, string text)
        {
            var saved = Path.Combine(backup, Path.GetRelativePath(wtf, path));
            if (File.Exists(path) && !File.Exists(saved))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(path, saved);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        }

        if (parts.HasFlag(CharacterParts.GameOptions))
        {
            var source = Path.Combine(from.Folder(gameFolder), "config-cache.wtf");
            foreach (var target in others.Select(t => (Character: t, Path: Path.Combine(t.Folder(gameFolder), "config-cache.wtf")))
                .Where(t => File.Exists(source) && File.Exists(t.Path)))
            {
                var (text, changed) = MergeCVars(Read(source), Read(target.Path));
                if (changed.Count > 0)
                {
                    Save(target.Path, text);
                    changes.Add($"{target.Character.Name}: {changed.Count} game option{(changed.Count == 1 ? "" : "s")}");
                }
            }
        }

        if (parts.HasFlag(CharacterParts.AddonProfiles))
        {
            var sourceFolder = Path.Combine(wtf, "Account", from.Account, "SavedVariables");
            IEnumerable<string> files = Directory.Exists(sourceFolder)
                ? Directory.EnumerateFiles(sourceFolder, "*.lua").Order(StringComparer.OrdinalIgnoreCase)
                : [];
            var shared = others.ToDictionary(t => t, _ => new List<string>());
            foreach (var file in files)
            {
                var sourceText = Read(file);
                foreach (var account in others.GroupBy(t => t.Account, StringComparer.OrdinalIgnoreCase))
                {
                    var targetPath = Path.Combine(wtf, "Account", account.Key, "SavedVariables", Path.GetFileName(file));
                    var sameAccount = account.First().SameAccount(from);
                    var text = sameAccount ? sourceText : File.Exists(targetPath) ? Read(targetPath) : null;
                    var changedAny = false;
                    foreach (var target in account)
                    {
                        var (next, changed) = ShareProfiles(sameAccount ? text! : sourceText, text, from.ProfileKey, target.ProfileKey, sameAccount);
                        if (changed)
                        {
                            text = next;
                            changedAny = true;
                            shared[target].Add(Path.GetFileNameWithoutExtension(file));
                        }
                    }
                    if (changedAny)
                    {
                        Save(targetPath, text!);
                    }
                }
            }
            changes.AddRange(shared.Where(s => s.Value.Count > 0).Select(s =>
                $"{s.Key.Name}: {(s.Key.SameAccount(from) ? "linked to" : "copied")} {from.Name}'s profile in {string.Join(", ", s.Value)}"));
        }

        var copied = (parts.HasFlag(CharacterParts.EnabledAddons) ? ["AddOns.txt"] : Array.Empty<string>())
            .Concat(parts.HasFlag(CharacterParts.Layout) ? LayoutFiles : []).ToList();
        foreach (var target in others)
        {
            var names = copied.Where(name =>
            {
                var src = Path.Combine(from.Folder(gameFolder), name);
                var dst = Path.Combine(target.Folder(gameFolder), name);
                return File.Exists(src) && (!File.Exists(dst) || !File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst)));
            }).ToList();
            names.ForEach(name => Save(Path.Combine(target.Folder(gameFolder), name), Read(Path.Combine(from.Folder(gameFolder), name))));
            if (names.Count > 0)
            {
                changes.Add($"{target.Name}: copied {string.Join(", ", names)}");
            }
        }

        return new CharacterCopyResult(changes, Directory.Exists(backup) ? backup : null);
    }

    // Makes targetKey use the profile sourceKey uses, in every AceDB table of sourceText that knows sourceKey.
    // targetText is the file to change (null when the target's account has none yet); unless it's the same file as
    // sourceText, the profile's table (and each namespace's) is copied into it too. Returns the new text and whether
    // it changed.
    public static (string Text, bool Changed) ShareProfiles(string sourceText, string? targetText, string sourceKey, string targetKey,
        bool sameFile)
    {
        var source = SplitLines(sourceText);
        var target = SplitLines(targetText ?? "");
        var newline = (targetText ?? sourceText).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var copyTables = !sameFile;
        foreach (var (name, start, end) in TopLevelTables(source))
        {
            var keys = FindTable(source, start, end, 1, "profileKeys");
            var profile = keys is { } k
                ? source.Skip(k.Start + 1).Take(k.End - k.Start - 1).Select(l => KeyLine.Match(l))
                    .FirstOrDefault(m => m.Success && m.Groups[1].Value == sourceKey)?.Groups[2].Value
                : null;
            if (profile is not null)
            {
                var global = FindGlobal(target, name) ?? AppendGlobal(target, name, newline);
                SetProfileKey(target, global, targetKey, profile, newline);
                if (copyTables)
                {
                    CopyProfileTable(source, (start, end), target, name, 1, profile, newline);
                    var namespaces = FindTable(source, start, end, 1, "namespaces");
                    var modules = namespaces is { } ns ? ChildTables(source, ns, 2) : [];
                    foreach (var module in modules)
                    {
                        var moduleTable = FindTable(source, namespaces!.Value.Start, namespaces.Value.End, 2, module)!.Value;
                        CopyNamespaceProfile(source, moduleTable, target, name, module, profile, newline);
                    }
                }
            }
        }
        var text = string.Concat(target);
        return (text, text != (targetText ?? ""));
    }

    private static void SetProfileKey(List<string> lines, (int Start, int End) global, string key, string profile, string newline)
    {
        var keys = EnsureTable(lines, global, 1, "profileKeys", newline);
        var entry = $"\t\t[\"{key}\"] = \"{profile}\",{newline}";
        var existing = Enumerable.Range(keys.Start + 1, keys.End - keys.Start - 1)
            .FirstOrDefault(i => KeyLine.Match(lines[i]) is { Success: true } m && m.Groups[1].Value == key, -1);
        if (existing < 0)
        {
            lines.Insert(keys.End, entry);
        }
        else if (KeyLine.Match(lines[existing]).Groups[2].Value != profile)
        {
            lines[existing] = entry;
        }
    }

    // Puts source's profiles[profile] (inside the table at depth - 1) into target's, replacing the old one.
    private static void CopyProfileTable(List<string> source, (int Start, int End) sourceParent, List<string> target, string global,
        int depth, string profile, string newline)
    {
        var sourceProfiles = FindTable(source, sourceParent.Start, sourceParent.End, depth, "profiles");
        var block = sourceProfiles is { } sp ? FindTable(source, sp.Start, sp.End, depth + 1, profile) : null;
        var parent = FindGlobal(target, global)!.Value;
        var profiles = EnsureTable(target, parent, depth, "profiles", newline);
        ReplaceTable(target, profiles, depth + 1, profile, block is { } b ? Lines(source, b, newline) : null);
    }

    private static void CopyNamespaceProfile(List<string> source, (int Start, int End) sourceModule, List<string> target, string global,
        string module, string profile, string newline)
    {
        var sourceProfiles = FindTable(source, sourceModule.Start, sourceModule.End, 3, "profiles");
        var block = sourceProfiles is { } sp ? FindTable(source, sp.Start, sp.End, 4, profile) : null;
        var namespaces = EnsureTable(target, FindGlobal(target, global)!.Value, 1, "namespaces", newline);
        var moduleTable = EnsureTable(target, namespaces, 2, module, newline);
        var profiles = EnsureTable(target, moduleTable, 3, "profiles", newline);
        ReplaceTable(target, profiles, 4, profile, block is { } b ? Lines(source, b, newline) : null);
    }

    private static List<string> Lines(List<string> lines, (int Start, int End) block, string newline) =>
        lines.Skip(block.Start).Take(block.End - block.Start + 1).Select(l => l.TrimEnd('\r', '\n') + newline).ToList();

    private static void ReplaceTable(List<string> lines, (int Start, int End) parent, int depth, string key, List<string>? block)
    {
        var old = FindTable(lines, parent.Start, parent.End, depth, key);
        var at = old?.Start ?? parent.End;
        if (old is { } o)
        {
            lines.RemoveRange(o.Start, o.End - o.Start + 1);
        }
        lines.InsertRange(at, block ?? []);
    }

    private static (int Start, int End) EnsureTable(List<string> lines, (int Start, int End) parent, int depth, string key, string newline)
    {
        var found = FindTable(lines, parent.Start, parent.End, depth, key);
        if (found is null)
        {
            var tabs = new string('\t', depth);
            lines.InsertRange(parent.End, [$"{tabs}[\"{key}\"] = {{{newline}", $"{tabs}}},{newline}"]);
        }
        return found ?? (parent.End, parent.End + 1);
    }

    // The table ["key"] = { ... } written at this depth between start and end (exclusive), as its first and last lines.
    private static (int Start, int End)? FindTable(List<string> lines, int start, int end, int depth, string key)
    {
        var tabs = new string('\t', depth);
        for (var i = start + 1; i < end; i++)
        {
            if (lines[i].StartsWith(tabs + "[", StringComparison.Ordinal) && TableKey.Match(lines[i]) is { Success: true } m
                && m.Groups[1].Value == key)
            {
                return (i, CloseOf(lines, i, depth));
            }
        }
        return null;
    }

    private static IReadOnlyList<string> ChildTables(List<string> lines, (int Start, int End) parent, int depth)
    {
        var tabs = new string('\t', depth);
        var names = new List<string>();
        for (var i = parent.Start + 1; i < parent.End; i++)
        {
            if (lines[i].StartsWith(tabs + "[", StringComparison.Ordinal) && TableKey.Match(lines[i]) is { Success: true } m)
            {
                names.Add(m.Groups[1].Value);
                i = CloseOf(lines, i, depth);
            }
        }
        return names;
    }

    private static int CloseOf(List<string> lines, int open, int depth)
    {
        var close = new string('\t', depth) + "}";
        for (var i = open + 1; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(close, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return lines.Count - 1;
    }

    private static IEnumerable<(string Name, int Start, int End)> TopLevelTables(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TopLevelTable.Match(lines[i]) is { Success: true } m)
            {
                var end = CloseOf(lines, i, 0);
                yield return (m.Groups[1].Value, i, end);
                i = end;
            }
        }
    }

    private static (int Start, int End)? FindGlobal(List<string> lines, string name) =>
        TopLevelTables(lines).Where(t => t.Name == name).Select(t => ((int, int)?)(t.Start, t.End)).FirstOrDefault();

    private static (int Start, int End) AppendGlobal(List<string> lines, string name, string newline)
    {
        if (lines.Count > 0 && !lines[^1].EndsWith('\n'))
        {
            lines[^1] += newline;
        }
        lines.Add($"{name} = {{{newline}");
        lines.Add($"}}{newline}");
        return (lines.Count - 2, lines.Count - 1);
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
