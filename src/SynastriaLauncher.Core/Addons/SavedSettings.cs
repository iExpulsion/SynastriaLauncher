namespace SynastriaLauncher.Core.Addons;

// One saved settings file: WTF\Account\<account>\SavedVariables\<addon>.lua (or .lua.bak), or the same under
// <realm>\<character>. Character is null for the account-wide file.
public sealed record SavedSettingsFile(string Path, string Addon, string Account, string? Character);

// The client names an addon's saved settings files after its folder in AddOns and never removes them, so removed
// addons leave theirs behind. Moving them aside puts them under WTF\_RemovedAddonSettings\<date> with their WTF path,
// so putting a file back where it was restores it.
public static class SavedSettings
{
    public const string MovedFolderName = "_RemovedAddonSettings";

    public static string WtfPath(string gameFolder) => Path.Combine(gameFolder, "WTF");

    public static string MovedPath(string gameFolder) => Path.Combine(WtfPath(gameFolder), MovedFolderName);

    public static IReadOnlyList<SavedSettingsFile> All(string gameFolder)
    {
        var accounts = Path.Combine(WtfPath(gameFolder), "Account");
        var folders = Directory.Exists(accounts)
            ? Directory.EnumerateDirectories(accounts).SelectMany(account => CharacterFolders(account)
                .Select(c => (Folder: Path.Combine(c.Path, "SavedVariables"), Account: Path.GetFileName(account), c.Character))
                .Prepend((Folder: Path.Combine(account, "SavedVariables"), Account: Path.GetFileName(account), Character: (string?)null)))
            : [];
        return folders.Where(f => Directory.Exists(f.Folder))
            .SelectMany(f => Directory.EnumerateFiles(f.Folder)
                .Select(p => (Path: p, Addon: AddonOf(System.IO.Path.GetFileName(p))))
                .Where(p => p.Addon is not null)
                .Select(p => new SavedSettingsFile(p.Path, p.Addon!, f.Account, f.Character)))
            .OrderBy(f => f.Addon, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The files of the given addon folders, on every account and character.
    public static IReadOnlyList<SavedSettingsFile> Of(string gameFolder, IEnumerable<string> addonFolders)
    {
        var names = addonFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All(gameFolder).Where(f => names.Contains(f.Addon)).ToList();
    }

    // Files of addons that have no folder in AddOns. The game's own addons (Blizzard_*) load from its archives, so
    // theirs are never left over.
    public static IReadOnlyList<SavedSettingsFile> Leftover(string gameFolder)
    {
        var addOns = GameFolder.AddOnsPath(gameFolder);
        return All(gameFolder)
            .Where(f => !f.Addon.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)
                && !Directory.Exists(Path.Combine(addOns, f.Addon)))
            .ToList();
    }

    // Moves the files under WTF\_RemovedAddonSettings\<date>, keeping their path inside WTF. A file already moved
    // there the same day keeps its place; the newer one gets a numbered name beside it.
    public static string MoveAside(string gameFolder, IEnumerable<SavedSettingsFile> files, DateTime now)
    {
        var wtf = WtfPath(gameFolder);
        var target = Path.Combine(MovedPath(gameFolder), now.ToString("yyyy-MM-dd"));
        foreach (var file in files.Where(f => File.Exists(f.Path)))
        {
            var destination = FreePath(Path.Combine(target, Path.GetRelativePath(wtf, file.Path)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file.Path, destination);
        }
        return target;
    }

    public static IReadOnlyList<string> Addons(IEnumerable<SavedSettingsFile> files) =>
        files.Select(f => f.Addon).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<(string Path, string Character)> CharacterFolders(string account) =>
        Directory.EnumerateDirectories(account)
            .Where(realm => !string.Equals(Path.GetFileName(realm), "SavedVariables", StringComparison.OrdinalIgnoreCase))
            .SelectMany(Directory.EnumerateDirectories)
            .Select(c => (c, Path.GetFileName(c)));

    private static string? AddonOf(string fileName) =>
        fileName.EndsWith(".lua.bak", StringComparison.OrdinalIgnoreCase) ? fileName[..^".lua.bak".Length]
        : fileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? fileName[..^".lua".Length]
        : null;

    private static string FreePath(string path)
    {
        var free = path;
        var extension = path.EndsWith(".lua.bak", StringComparison.OrdinalIgnoreCase) ? ".lua.bak" : Path.GetExtension(path);
        var stem = path[..^extension.Length];
        for (var n = 2; File.Exists(free); n++)
        {
            free = $"{stem} ({n}){extension}";
        }
        return free;
    }
}
