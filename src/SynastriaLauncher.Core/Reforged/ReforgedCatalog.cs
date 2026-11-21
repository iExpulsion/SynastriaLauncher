using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Core.Reforged;

public enum ReforgedSection
{
    Core,
    Optional,
    Audio,
}

// One way to get a part: a bucket path per file it installs.
public sealed record ReforgedVariant(string Id, string Name, IReadOnlyList<string> BucketPaths);

// A row on the page. Its files go into the game's Data folder under their own names; a part with several variants
// installs the same file names from different bucket paths.
public sealed record ReforgedPart(
    string Id,
    string Name,
    string Description,
    ReforgedSection Section,
    IReadOnlyList<string> Files,
    IReadOnlyList<ReforgedVariant> Variants,
    bool DefaultOn,
    IReadOnlyList<string>? Requires = null,
    string? Requirement = null)
{
    // Launcher patches the part doesn't work without.
    public IReadOnlyList<string> Needs => Requires ?? [];
}

// Project Reforged's downloads, from https://projectreforged.github.io/wotlk/downloads/ (nothing is re-hosted).
public static class ReforgedCatalog
{
    public const string BucketBase = "https://pub-0f05631d243e4046993fc02ca7be9542.r2.dev/Wrath%20of%20the%20Lich%20King/";
    public const string Site = "https://projectreforged.github.io/wotlk/";
    public const string DownloadsPage = "https://projectreforged.github.io/wotlk/downloads/";
    public const string Discord = "https://discord.gg/jnvkayMbqJ";
    public const string KoFi = "https://ko-fi.com/projectreforged";

    private static ReforgedPart Single(string id, string name, string description, ReforgedSection section, char letter, bool on) =>
        new(id, name, description, section, [$"patch-{letter}.mpq"], [new("on", name, [$"patches/patch-{letter}.mpq"])], on);

    public static IReadOnlyList<ReforgedPart> Parts { get; } =
    [
        new("characters", "Player Characters and NPCs", "Remade character and NPC models.", ReforgedSection.Core,
            ["patch-A.mpq"],
            [new("hd", "HD Models", ["patches/patch-A.mpq"]), new("sd", "SD Models", ["patches/extras/patch-A.mpq"])], true),
        Single("creatures", "Creatures", "Remade creature models.", ReforgedSection.Core, 'C', true)
            with { Requirement = "Needs Player Characters and NPCs" },
        new("world", "Buildings, Doodads and Environment", "The world set: buildings, props and terrain, which only work together.",
            ReforgedSection.Core, ["patch-B.mpq", "patch-D.mpq", "patch-E.mpq"],
            [new("on", "World", ["patches/patch-B.mpq", "patches/patch-D.mpq", "patches/patch-E.mpq"])], true),
        Single("gear", "Gear and Weapons", "Remade armor and weapon models.", ReforgedSection.Core, 'G', true),
        Single("interface", "Interface", "Interface visuals and UI enhancements, with new login and character screens.",
            ReforgedSection.Core, 'I', true) with { Requires = [PatchIds.InterfaceEdit], Requirement = "Needs the Interface Edit patch" },
        Single("maps", "Maps and Loading Screens", "New maps and loading screens.", ReforgedSection.Optional, 'M', false),
        Single("nights", "Darker Nights", "Darker nighttime lighting.", ReforgedSection.Optional, 'N', false),
        Single("particles", "Spell Particle Effects", "Remade spell effects.", ReforgedSection.Optional, 'P', false),
        Single("orcs", "Upright Orcs", "Orcs stand up straight.", ReforgedSection.Optional, 'U', false)
            with { Requirement = "Needs Player Characters and NPCs, and Creatures" },
        new("sounds", "Sounds and Music", "New sounds and music.",
            ReforgedSection.Audio, ["patch-S.mpq"],
            [new("standard", "Standard", ["patches/patch-S.mpq"]), new("standalone", "Standalone", ["patches/extras/patch-S.mpq"])], false,
            Requirement: "The Standard version needs Maps and Loading Screens"),
    ];

    public static ReforgedPart Get(string id) => Parts.First(p => p.Id == id);

    // Patches in Project Reforged's recommended patcher selection that the launcher has and no part strictly needs:
    // its view distance unlocks (farclip, horizon, environment detail, ground clutter) and no zoom transparency.
    public static IReadOnlyList<string> Recommended { get; } = [PatchIds.GraphicsLimits, PatchIds.NoZoomFade];

    public static IReadOnlyList<ReforgedPart> InstalledParts(string gameFolder) =>
        Parts.Where(p => p.Files.Any(f => File.Exists(Path.Combine(gameFolder, "Data", f)))).ToList();

    // What's missing for the given parts: each required patch that's off with the parts needing it, and the
    // recommended patches that are off (only when any part is in use).
    public static (IReadOnlyList<(string Patch, IReadOnlyList<ReforgedPart> Parts)> Required, IReadOnlyList<string> Recommended)
        MissingPatches(IReadOnlyCollection<ReforgedPart> parts, IReadOnlyCollection<string> applied) =>
        (parts.SelectMany(p => p.Needs.Select(id => (Id: id, Part: p))).Where(x => !applied.Contains(x.Id))
                .GroupBy(x => x.Id).Select(g => (g.Key, (IReadOnlyList<ReforgedPart>)g.Select(x => x.Part).ToList())).ToList(),
            parts.Count == 0 ? [] : Recommended.Where(id => !applied.Contains(id)).ToList());

    // The page's combination rules. Selection maps part id to its variant id, null when off.
    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string?> selection)
    {
        bool On(string id) => selection.GetValueOrDefault(id) is not null;
        var problems = new List<string>();
        if (On("creatures") && !On("characters"))
        {
            problems.Add("Creatures needs Player Characters and NPCs");
        }
        if (On("orcs") && !(On("characters") && On("creatures")))
        {
            problems.Add("Upright Orcs needs Player Characters and NPCs and Creatures");
        }
        if (selection.GetValueOrDefault("sounds") == "standard" && !On("maps"))
        {
            problems.Add("Standard Sounds and Music needs Maps and Loading Screens (or choose Standalone)");
        }
        return problems;
    }
}
