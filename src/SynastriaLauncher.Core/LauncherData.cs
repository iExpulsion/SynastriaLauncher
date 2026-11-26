using System.Text.Json;
using SynastriaLauncher.Core.Addons;
using SynastriaLauncher.Core.Mods;

namespace SynastriaLauncher.Core;

// Everything the launcher remembers, in one JSON file per Windows user.
public sealed class LauncherData
{
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SynastriaLauncher");

    public static string DefaultPath { get; } = Path.Combine(DefaultFolder, "launcher.json");

    public string? GameFolder { get; set; }
    public bool KeepAddonBackups { get; set; }
    public List<AddonSource> UserAddons { get; set; } = [];
    public List<string> HiddenRepos { get; set; } = [];

    // Addons the player keeps on their own version (local fixes, a pinned release): Update All passes them by.
    public List<string> HeldRepos { get; set; } = [];

    // Addons with releases that the player runs from their latest commits instead (a test or development version).
    public List<string> LatestCommitRepos { get; set; } = [];

    public bool UsesLatestCommits(string repo) => LatestCommitRepos.Any(r => SameRepo(r, repo));

    // Page sections the player folded, as "Page/Section".
    public List<string> CollapsedSections { get; set; } = [];
    public Dictionary<string, InstallRecord> Installed { get; set; } = [];
    public Dictionary<string, ModRecord> Mods { get; set; } = [];

    // The WowExt package version installed in the game folder, once the launcher has installed or recognized one.
    public int? WowExtVersion { get; set; }

    // Game accounts for auto login, and what the Launch button logs in as: an account (null: none) and one of its
    // characters (null: character select).
    public List<Accounts.SavedAccount> Accounts { get; set; } = [];
    public string? LaunchAccount { get; set; }
    public string? LaunchCharacter { get; set; }

    // The Launch button's target, when its account (and character) still exist.
    public Accounts.LaunchTarget? LaunchTarget =>
        FindAccount(LaunchAccount) is { } account
            ? new Accounts.LaunchTarget(account.Name,
                account.Characters.FirstOrDefault(c => LaunchCharacter is not null && Core.Accounts.SavedAccount.SameCharacter(c, LaunchCharacter)))
            : null;

    public Accounts.SavedAccount? FindAccount(string? name) =>
        name is null ? null : Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    // Characters whose chat windows are kept the same (WtfCharacter.Key), synced before each game start.
    public List<string> SharedChatCharacters { get; set; } = [];

    // WoW Dungeon Maps parts the launcher installed, by part id, with the release tag.
    public Dictionary<string, string> WdmTags { get; set; } = [];

    // The curated list, then the player's own additions, then addons the launcher installed that have since left the
    // curated list: those stay tracked and updated, just no longer offered to anyone new.
    public IEnumerable<AddonSource> AddonSources(IReadOnlyList<AddonSource> curated)
    {
        var user = UserAddons.Where(u => !curated.Any(c => SameRepo(c.Id, u.Id))).ToList();
        var listed = curated.Concat(user).ToList();
        var dropped = Installed.Where(p => p.Value.Folders is { Count: > 0 } && !listed.Any(s => SameRepo(s.Id, p.Key)))
            .Select(p => new AddonSource(AddonSource.RepoOf(p.Key), p.Value.Name ?? p.Value.Folders![0], p.Value.Folders!,
                null, true, "No longer on the launcher's list. It's still kept up to date.", Zip: p.Value.Zip));
        return listed.Concat(dropped);
    }

    // Compares addon ids (AddonSource.Id: the repo, or repo:zip for a collection's addon), which the saved lists and
    // install records hold.
    public static bool SameRepo(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static LauncherData Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        LauncherData? data = null;
        if (File.Exists(file))
        {
            try
            {
                data = JsonSerializer.Deserialize(File.ReadAllText(file), CoreJson.Default.LauncherData);
            }
            catch (JsonException)
            {
                File.Copy(file, file + ".broken", overwrite: true);
            }
        }
        var loaded = data ?? new LauncherData();
        // Repo names compare without case everywhere else, so the install records do too.
        var records = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repo, record) in loaded.Installed)
        {
            records[repo] = record;
        }
        loaded.Installed = records;
        return loaded;
    }

    public void Save(string? path = null)
    {
        var file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, CoreJson.Default.LauncherData));
        File.Move(temp, file, overwrite: true);
    }
}
