using System.Text.Json;
using System.Text.Json.Serialization;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Addons;

public sealed record AddonEntry(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("folders")] IReadOnlyList<string> Folders,
    [property: JsonPropertyName("toc")] string? Toc,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("requires")] IReadOnlyList<string>? Requires = null,
    [property: JsonPropertyName("library")] bool Library = false)
{
    public AddonSource ToSource() => new(Repo, Name, Folders, Toc, true, Description, Requires, Library);
}

// The curated addons: Addons/addons.json, built in and published in the launcher's repo (LiveList).
public sealed record AddonCatalog(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("addons")] IReadOnlyList<AddonEntry> Addons,
    [property: JsonPropertyName("minLauncher")] string? MinLauncher = null) : ILiveList
{
    public const string RemoteUrl = LiveList.RepoFolder + "Addons/addons.json";

    public IReadOnlyList<AddonSource> Sources => Addons.Select(a => a.ToSource()).ToList();

    public static AddonCatalog Parse(string json) =>
        JsonSerializer.Deserialize(json, CoreJson.Default.AddonCatalog) ?? throw new JsonException("Empty addons list");

    public static AddonCatalog Embedded()
    {
        using var stream = typeof(AddonCatalog).Assembly.GetManifestResourceStream("SynastriaLauncher.Core.Addons.addons.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    // An addons.json beside launcher.json replaces the list (for testing).
    public static Task<AddonCatalog> LoadAsync(GitHubKeyless github, string overridePath, Version launcher,
        CancellationToken cancel = default) =>
        LiveList.LoadAsync(github, RemoteUrl, overridePath, Parse, Embedded(), launcher, cancel);

    // Only GitHub owner/repo names, single folder names inside AddOns, .toc paths inside the repo, and requirements
    // that are on the list, so a published list can't point the launcher anywhere else.
    public IReadOnlyList<string> Problems()
    {
        var repos = Addons.Select(a => a.Repo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var addon in Addons)
        {
            var label = addon.Repo;
            if (UserAddons.ParseRepo(addon.Repo) != addon.Repo || addon.Repo.Contains("://"))
            {
                problems.Add($"{label}: not an owner/repo name");
            }
            if (string.IsNullOrWhiteSpace(addon.Name) || string.IsNullOrWhiteSpace(addon.Description))
            {
                problems.Add($"{label}: needs a name and a description");
            }
            if (addon.Folders.Count == 0 || !addon.Folders.All(SafePaths.IsFolderName))
            {
                problems.Add($"{label}: folders must be single folder names");
            }
            if (addon.Toc is not null && !SafePaths.IsRelative(addon.Toc))
            {
                problems.Add($"{label}: the .toc path must stay inside the repo");
            }
            problems.AddRange((addon.Requires ?? []).Where(r => !repos.Contains(r) || r.Equals(addon.Repo, StringComparison.OrdinalIgnoreCase))
                .Select(r => $"{label}: requires {r}, which isn't another addon on the list"));
        }
        problems.AddRange(Addons.GroupBy(a => a.Repo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is listed twice"));
        problems.AddRange(Addons.SelectMany(a => a.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => (Folder: f, a.Repo)))
            .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is claimed by {string.Join(" and ", g.Select(x => x.Repo))}"));
        problems.AddRange(Addons.Where(a => InCycle(a.Repo)).Select(a => $"{a.Repo}: its requirements lead back to itself"));
        return problems;
    }

    // Follows requirements from repo; true when they come back to it.
    private bool InCycle(string repo)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new Queue<string>(Requirements(repo));
        var cycle = false;
        while (next.Count > 0 && !cycle)
        {
            var current = next.Dequeue();
            cycle = current.Equals(repo, StringComparison.OrdinalIgnoreCase);
            if (!cycle && seen.Add(current))
            {
                foreach (var further in Requirements(current))
                {
                    next.Enqueue(further);
                }
            }
        }
        return cycle;
    }

    private IEnumerable<string> Requirements(string repo) =>
        Addons.FirstOrDefault(a => a.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase))?.Requires ?? [];
}
