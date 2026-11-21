using System.Text.Json;
using System.Text.Json.Serialization;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Mods;

// One file a mod puts in the game folder. "{dlls}" in the path is the DLL Loader's folder. The file comes from
// url (an archive entry matched by file name at any depth when entry is set), or from content. Config files are
// written only when missing and kept on removal, since players edit them. A pinned sha256 must match.
// Presets apply only to config files; the first one is what a fresh install writes.
public sealed record ModFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("entry")] string? Entry = null,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("config")] bool Config = false,
    [property: JsonPropertyName("optional")] bool Optional = false,
    [property: JsonPropertyName("presets")] IReadOnlyList<ModPreset>? Presets = null,
    [property: JsonPropertyName("bundled")] string? Bundled = null);

// A complete config file shipped inside the launcher (Mods/Presets/<file>); one downloaded from someone else's
// site (url, or an archive entry with entry), kept in the preset cache; or neither, no config at all so the mod runs
// on its own built-in defaults.
public sealed record ModPreset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("entry")] string? Entry = null)
{
    public bool IsNone => File is null && Url is null;
    public bool IsDownloaded => File is null && Url is not null;
}

public sealed record ModDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("details")] string? Details,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("homepage")] string? Homepage,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("requires")] IReadOnlyList<string> Requires,
    [property: JsonPropertyName("files")] IReadOnlyList<ModFile> Files,
    [property: JsonPropertyName("origin")] string? Origin = null,
    [property: JsonPropertyName("onlyWith")] string? OnlyWith = null,
    [property: JsonPropertyName("builtFromSource")] bool BuiltFromSource = false,
    [property: JsonPropertyName("followsPart")] string? FollowsPart = null)
{
    // A fix for a Project Reforged part: not listed on the Mods page, installed while that part is on and removed
    // when it's off. Older launchers ignore the field and list it as a normal mod.
    public bool FollowsReforged => FollowsPart is not null;

    // Made for Synastria and shipped inside the launcher, versus a community mod downloaded from its author.
    public bool IsSynastria => Origin == "synastria";
}

// Things a mod can depend on that aren't patches: "reforged" is Project Reforged's graphics overhaul.
public static class ClientFeatures
{
    public const string Reforged = "reforged";

    // Project Reforged installs lettered patches (patch-A.mpq and up); the stock client numbers its own.
    public static bool HasReforged(string gameFolder)
    {
        var data = Path.Combine(gameFolder, "Data");
        return Directory.Exists(data) && Directory.EnumerateFiles(data, "patch-?.mpq")
            .Any(f => char.IsAsciiLetter(Path.GetFileNameWithoutExtension(f)[^1]));
    }

    public static bool Has(string? feature, string gameFolder) =>
        feature is null || feature == Reforged && HasReforged(gameFolder);
}

public sealed record ModManifest(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("mods")] IReadOnlyList<ModDefinition> Mods,
    [property: JsonPropertyName("minLauncher")] string? MinLauncher = null) : ILiveList
{
    public const string RemoteUrl = LiveList.RepoFolder + "Mods/mods.json";

    // Every file stays inside the game folder, downloads are https, and ids are unique.
    public IReadOnlyList<string> Problems()
    {
        var problems = Mods.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => $"{g.Key} is listed twice").ToList();
        problems.AddRange(Mods.SelectMany(m => m.Files.Where(f => !SafePaths.IsRelative(f.Path)).Select(f => $"{m.Id}: {f.Path} leaves the game folder")));
        var urls = Mods.SelectMany(m => m.Files.SelectMany(f => (f.Presets ?? []).Select(p => p.Url).Append(f.Url))
            .Where(u => u is not null).Select(u => (m.Id, Url: u!)));
        problems.AddRange(urls.Where(u => !u.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Select(u => $"{u.Id}: {u.Url} isn't an https download"));
        return problems;
    }

    public static ModManifest Parse(string json) =>
        JsonSerializer.Deserialize(json, CoreJson.Default.ModManifest) ?? throw new JsonException("Empty mods manifest");

    public static ModManifest Embedded()
    {
        using var stream = typeof(ModManifest).Assembly.GetManifestResourceStream("SynastriaLauncher.Core.Mods.mods.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    // A mods.json beside launcher.json replaces everything (for testing local packages through file:// URLs).
    public static Task<ModManifest> LoadAsync(GitHubKeyless github, string overridePath, Version launcher,
        CancellationToken cancel = default) =>
        LiveList.LoadAsync(github, RemoteUrl, overridePath, Parse, Embedded(), launcher, cancel);
}

public sealed record ModRecord(string Version, Dictionary<string, string> Hashes, DateTime InstalledAt);
