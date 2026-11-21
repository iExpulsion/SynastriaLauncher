using System.Text.RegularExpressions;

namespace SynastriaLauncher.Core.Wdm;

public enum WdmKind
{
    Patch,
    Addon,
}

// A row on the WoW Dungeon Maps page: a map patch (one MPQ per client language, "{locale}" in the asset name) or
// addons from one or more release zips. Needs lists the parts it doesn't work without.
public sealed record WdmPart(
    string Id,
    string Name,
    string Description,
    WdmKind Kind,
    string Repo,
    IReadOnlyList<string> Assets,
    IReadOnlyList<string> Folders,
    bool PreRelease = false,
    IReadOnlyList<string>? Needs = null,
    string? Requirement = null,
    string? Warning = null)
{
    public IReadOnlyList<string> NeededParts => Needs ?? [];

    public string AssetFor(string asset, string locale) => asset.Replace("{locale}", locale);

    // Where a patch goes: Data\<locale>\patch-<locale>-M.MPQ.
    public string PatchPath(string gameFolder, string locale) =>
        Path.Combine(gameFolder, "Data", locale, AssetFor(Assets[0], locale));
}

// Trimitor's WoW Dungeon Maps: Classic and TBC dungeon maps for 3.3.5a, the optional Caverns and Mines maps, and the
// addons that show them. WDM bundles the same Astrolabe as the release's standalone !Astrolabe, which is only for
// other addons that use it as a library, so it isn't installed. Project Reforged's Maps and Loading Screens (patch-M.mpq) already holds both map patches
// with the art at 4x, so the map patches are locked while it's installed.
public static partial class WdmCatalog
{
    public const string PatchRepo = "Trimitor/WDM-patch";
    public const string AddonRepo = "Trimitor/WDM-addons";
    public const string Map = "map";
    public const string Caves = "caves";
    public const string Addon = "wdm";

    public static IReadOnlyList<WdmPart> Parts { get; } =
    [
        new(Map, "Dungeon Maps", "56 maps from later expansions: Classic and TBC dungeons, continents and battlegrounds.",
            WdmKind.Patch, PatchRepo, ["patch-{locale}-M.MPQ"], []),
        new(Caves, "Caverns and Mines", "Extra maps for caves, mines, dungeon entrances, starting areas and hidden places.",
            WdmKind.Patch, PatchRepo, ["patch-{locale}-N.MPQ"], [], PreRelease: true, Needs: [Map, Addon],
            Requirement: "Needs Dungeon Maps and the WDM addon"),
        new(Addon, "WDM", "Dungeon level names, flight masters, dungeon entrances and zone levels on the map.",
            WdmKind.Addon, AddonRepo, ["WDM.zip"], ["WDM"]),
        new("mapster", "Mapster", "Nevcairiel's world map addon, with the new maps added to its lists.",
            WdmKind.Addon, AddonRepo, ["Mapster.zip"], ["Mapster"],
            Warning: "Doesn't fit well on Synastria: the world map's search panel on the right overlaps Mapster's own controls."),
        new("gathermate", "GatherMate", "Nevcairiel's gathering node tracker, with data for the cave maps.",
            WdmKind.Addon, AddonRepo, ["GatherMate.zip"], ["GatherMate"]),
        new("questhelper", "QuestHelper", "QuestHelper, with data for the cave maps.",
            WdmKind.Addon, AddonRepo, ["QuestHelper.zip"], ["QuestHelper"]),
    ];

    public static WdmPart Get(string id) => Parts.First(p => p.Id == id);

    [GeneratedRegex("^[a-z]{2}[A-Z]{2}$")]
    private static partial Regex LocaleName();

    [GeneratedRegex("""^\s*SET\s+locale\s+"([a-zA-Z]{4})"\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex LocaleSetting();

    // The client's language: the locale WTF\Config.wtf sets, when its Data subfolder exists, else the Data subfolder
    // holding a locale archive (in name order), else enUS.
    public static string Locale(string gameFolder)
    {
        var data = Path.Combine(gameFolder, "Data");
        var config = Path.Combine(gameFolder, "WTF", "Config.wtf");
        var set = File.Exists(config)
            ? File.ReadLines(config).Select(l => LocaleSetting().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).LastOrDefault()
            : null;
        var installed = Directory.Exists(data)
            ? Directory.EnumerateDirectories(data).Select(Path.GetFileName).OfType<string>()
                .Where(d => LocaleName().IsMatch(d) && File.Exists(Path.Combine(data, d, $"locale-{d}.MPQ"))).Order().ToList()
            : [];
        return installed.FirstOrDefault(d => d.Equals(set, StringComparison.OrdinalIgnoreCase)) ?? installed.FirstOrDefault() ?? "enUS";
    }

    // Reforged's Maps and Loading Screens holds both map patches already.
    public static bool ReforgedMaps(string gameFolder) => File.Exists(Path.Combine(gameFolder, "Data", "patch-M.mpq"));

    [GeneratedRegex("""<link[^>]*href="[^"]*/releases/tag/([^"]+)"[^>]*/>""")]
    private static partial Regex ReleaseLink();

    // Release tags from the repo's releases Atom feed, newest first (pre-releases included).
    public static IReadOnlyList<string> ParseReleaseTags(string atom) =>
        ReleaseLink().Matches(atom).Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).Distinct().ToList();
}
