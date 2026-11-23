namespace SynastriaLauncher.Core.Patching;

public enum PatchGroup
{
    QualityOfLife,
    Visuals,
    CombatText,
    Nameplates,
    Advanced,
}

public sealed record Replacement(string FindHex, string ReplaceHex)
{
    public byte[] Find { get; } = Bytes.FromHex(FindHex);
    public byte[] Replace { get; } = Bytes.FromHex(ReplaceHex);
}

public sealed record Routine(int Offset, byte[] Code)
{
    public Routine(int offset, params string[] hexLines) : this(offset, Bytes.FromHex(string.Join(" ", hexLines))) { }
}

public sealed record PatchDefinition(
    string Id,
    string Title,
    PatchGroup Group,
    string Summary,
    string Description,
    bool Experimental,
    IReadOnlyList<Replacement> Replacements,
    IReadOnlyList<Routine> Routines,
    bool Hidden = false,
    string? Credit = null,
    bool MemoryOnly = false,
    string? Warning = null);

public static class PatchIds
{
    public const string InterfaceEdit = "InterfaceEdit";
    public const string LargeAddressAware = "LargeAddressAware";
    public const string NoAutoAfk = "NoAutoAfk";
    public const string NoMailDelay = "NoMailDelay";
    public const string DllLoader = "DllLoader";
    public const string GraphicsLimits = "GraphicsLimits";
    public const string NoZoomFade = "NoZoomFade";
    public const string HdPortraits = "HdPortraits";
    public const string NameplateDistance = "NameplateDistance";
    public const string NameplateSort = "NameplateSort";
    public const string NameplateStack = "NameplateStack";
    public const string ShortNumbers = "ShortNumbers";
    public const string DamageColors = "DamageColors";
    public const string GroundTargets = "GroundTargets";
    public const string CursorOptimizations = "CursorOptimizations";
}

// Patches are planned and applied in this order.
public static class PatchCatalog
{
    internal const int StackSpacingOffset = 0xB08;
    internal const int ColorTableOffset = 0x240;
    internal const int DllFolderOffset = 0x1A00;
    internal const uint OriginalEntry = 0x1000;
    internal const uint LoaderEntry = PatchSection.Rva + 0x1900;
    internal const string NameplateDistanceAnchor = "2E 74 01 37 00 00 C8 43";
    private const string St0ny = "From St0ny's AIO WoW EXE Patcher";

    // What Recommended Setup switches on: the safe everyday patches, without view distances, nameplates or ground
    // target macros.
    public static IReadOnlyList<string> RecommendedSetup { get; } =
    [
        PatchIds.NoAutoAfk, PatchIds.NoMailDelay, PatchIds.InterfaceEdit, PatchIds.DllLoader,
        PatchIds.ShortNumbers, PatchIds.DamageColors,
    ];

    public static IReadOnlyList<PatchDefinition> All { get; } =
    [
        new(PatchIds.InterfaceEdit, "Interface Edit", PatchGroup.Advanced,
            "Lets the game load modified interface files, such as custom login and character select screens",
            "Lets the client load modified interface files instead of rejecting them.",
            false,
            [
                new("04 85 C0 74 39 56", "04 85 C0 EB 39 56"),
                new("C0 FF 85 C0 75 05 5E 8B", "C0 FF 85 C0 EB 05 5E 8B"),
                new("B6 C0 FF B8 01 00 00 00", "B6 C0 FF B8 03 00 00 00"),
                new("C0 FF 5F B8 01 00 00 00", "C0 FF 5F B8 03 00 00 00"),
                new("B8 01 00 00 00 7F 12 83 C8 FF F7", "B8 01 00 00 00 EB 12 83 C8 FF F7"),
                new("C0 5F 83 C0 03 5E 8B E5 5D C3 CC", "C0 5F B8 03 00 00 00 EB ED C3 CC"),
            ],
            []),
        new(PatchIds.LargeAddressAware, "Large Address Aware", PatchGroup.Advanced,
            "Lets the game use up to 4 GB of memory",
            "Lets the 32-bit client use up to 4 GB of memory.",
            false, [], [], Hidden: true),
        new(PatchIds.CursorOptimizations, "Cursor Optimizations", PatchGroup.QualityOfLife,
            "Keeps the frame rate up when you move the mouse",
            "Each time the mouse moves, the game hides the Windows cursor and has the graphics card show its own cursor again, "
                + "which is slow, and it keeps sending its cursor to the graphics card even when it looks the same, which DXVK "
                + "makes slow too. This leaves the cursor in place while it's shown and skips the sends that wouldn't change "
                + "anything. Only works with patches loaded in memory.",
            true, [], [], MemoryOnly: true),
        new(PatchIds.NoAutoAfk, "No Auto AFK", PatchGroup.QualityOfLife,
            "No more being marked AFK or logged out for sitting idle",
            "Stops the client from marking you AFK after 5 idle minutes and logging you out after 30.",
            false,
            [
                new("2B 05 A4 99 B4 00 8D 88 20 6C FB FF 85 C9 0F 8C ED FD FF FF",
                    "2B 05 A4 99 B4 00 8D 88 20 6C FB FF 85 C9 E9 EE FD FF FF 90"),
            ],
            []),
        new(PatchIds.NoMailDelay, "No Mail Refresh Delay", PatchGroup.QualityOfLife,
            "Mail that refreshes right away",
            "Lets the mailbox refresh right away instead of waiting 60 seconds between inbox checks.",
            false,
            [
                new("05 60 EA 00 00 68 3A 02 00 00", "05 01 00 00 00 68 3A 02 00 00"),
            ],
            []),
        new(PatchIds.DllLoader, "DLL Loader", PatchGroup.Advanced,
            "Loads the launcher's patches and DLL mods when the game starts",
            "Loads SynastriaLauncher.dll from the game folder, then every DLL in the chosen folder (inside the game folder), when the game starts.",
            false,
            [],
            [
                new(0x1900,
                    "60 E8 3A 05 00 00 68 04 01 00 00 68 80 FA DF 00 6A 00 FF 15 A0 F1 9D 00 8D B8 80 FA DF 00 4F 81",
                    "FF 80 FA DF 00 0F 82 88 00 00 00 80 3F 5C 75 EE E8 8B 00 00 00 90 AC AA 84 C0 75 FA C6 47 FF 5C",
                    "89 3D 40 FA DF 00 C7 07 2A 2E 64 6C 66 C7 47 04 6C 00 68 00 FD DF 00 68 80 FA DF 00 FF 15 84 F3",
                    "9D 00 83 F8 FF 74 4C 89 C3 F6 05 00 FD DF 00 10 75 2A BE 2C FD DF 00 8B 3D 40 FA DF 00 AC AA 84",
                    "C0 75 FA 8B 47 FB 0D 00 20 20 20 3D 2E 64 6C 6C 75 0A 68 80 FA DF 00 E8 03 05 00 00 68 00 FD DF",
                    "00 53 FF 15 80 F3 9D 00 85 C0 75 BD 53 FF 15 7C F3 9D 00 61 E9 47 16 60 FF"),
                new(0x19C0,
                    "47 57 BE E0 F9 DF 00 AC AA 84 C0 75 FA 68 80 FA DF 00 E8 C8 04 00 00 5F BE 00 FA DF 00 C3 00 00",
                    "53 79 6E 61 73 74 72 69 61 4C 61 75 6E 63 68 65 72 2E 64 6C 6C 00"),
                new(0x1E40,
                    "68 40 FF DF 00 FF 15 B0 F1 9D 00 85 C0 74 4F 89 C3 8B 4B 3C 8B 6C 0B 7C 8B 4C 0B 78 01 D9 8B 51",
                    "18 4A 78 3A 8B 71 20 01 DE 8B 34 96 01 DE BF 50 FF DF 00 51 B9 0F 00 00 00 F3 A6 59 75 E3 8B 71",
                    "24 01 DE 0F B7 14 56 8B 71 1C 01 DE 8B 04 96 01 D8 89 C2 29 CA 39 EA 72 05 A3 44 FA DF 00 C3 A1",
                    "44 FA DF 00 85 C0 74 0D 6A 08 6A 00 FF 74 24 0C FF D0 C2 04 00 FF 74 24 04 FF 15 48 F2 9D 00 C2",
                    "04 00"),
                new(0x1F40,
                    "6B 65 72 6E 65 6C 33 32 2E 64 6C 6C 00 00 00 00 4C 6F 61 64 4C 69 62 72 61 72 79 45 78 41 00"),
            ]),
        new(PatchIds.GraphicsLimits, "Graphics Limits", PatchGroup.Visuals,
            "Higher limits on the video settings sliders (view distance, ground clutter and more)",
            "Raises the view distance, horizon, object detail and ground clutter limits, and the video menu sliders to match.",
            false,
            [
                new("37 43 AB EA 45 44 AB EA C5 44 90 C2", "37 43 00 40 1C 46 00 40 1C 46 90 C2"),
                new("DD D8 D9 05 F8 8C 9E 00 D8 D1 DF E0", "DD D8 D9 05 7C 04 A1 00 D8 D1 DF E0"),
                new("05 7A 12 51 DD D9 D9 1C 24 E8 D9 18", "05 7A 12 51 DD D8 D9 1C 24 E8 D9 18"),
                new("00 00 C0 40 00 00 0C 43 00 00 00 C0", "00 00 C0 40 AB EA 45 45 00 00 00 C0"),
                new("68 FF FF FF 7F 68 A0 57 9F 00 56 E8 2A 07 29 00 85 C0 75 20 DD 05 D8 57 9F 00",
                    "53 89 F3 BE A0 FF DF 00 E8 2D 1F 92 00 5B 83 7E FC 00 74 20 D9 46 10 90 90 90"),
                new("68 FF FF FF 7F 68 A0 57 9F 00 56 E8 86 98 25 00 85 C0 75 20 DD 05 D8 57 9F 00",
                    "53 89 F3 BE A0 FF DF 00 E8 89 B0 8E 00 5B 83 7E FC 00 74 20 D9 46 10 90 90 90"),
            ],
            [
                new(0x1F80, "AD 85 C0 74 0C 56 50 53 E8 F3 E7 96 FF 85 C0 75 EF C3"),
                new(0x1FA0,
                    "A0 57 9F 00 BC F3 A3 00 38 F4 A3 00 60 F4 A3 00 00 00 00 00 00 D0 1A 45 00 00 20 40 00 00 7A 43",
                    "00 00 80 43"),
            ],
            Credit: St0ny,
            Warning: "Heavy with Project Reforged: higher view distances load much more of the world, which can cut frame rates or crash the game, especially on AMD graphics cards. Keep the sliders low if that happens."),
        new(PatchIds.NoZoomFade, "No Zoom Transparency", PatchGroup.Visuals,
            "Your character no longer turns see-through when zoomed in close",
            "Keeps your character from turning see-through when the camera is zoomed in close.",
            false,
            [
                new("8A 55 FC 88 96 CB 00 00 00 8B B6 5C", "8A 55 FC 90 90 90 90 90 90 8B B6 5C"),
            ],
            [],
            Credit: St0ny),
        new(PatchIds.HdPortraits, "HD Portraits", PatchGroup.Visuals,
            "Sharper character portraits on unit frames",
            "Renders unit frame portraits at 256x256 instead of 64x64, so they look much sharper.",
            false,
            [
                new("89 45 FC E8 93 0B EA FF 83 C4 2C A3", "89 45 FC E8 23 7F 7E 00 83 C4 2C A3"),
                new("40 6A 40 E8 89 F6 E9 FF 83 C4 24 89", "40 6A 40 E8 AE 64 7E 00 83 C4 24 89"),
                new("83 C4 08 B8 40 00 00 00 8D 55 F0 89", "83 C4 08 B8 00 01 00 00 8D 55 F0 89"),
                new("8D 55 E4 BF 40 00 00 00 53 52 89 45", "8D 55 E4 BF 00 01 00 00 53 52 89 45"),
                new("CC 55 8B EC 81 EC 04 05 00 00 53 56", "CC E9 95 89 7E 00 90 90 90 90 53 56"),
            ],
            [
                new(0x2000, "00 00 00 00 00 00 01 00 00 10 E0 00"),
                new(0x2010,
                    "C7 44 24 08 00 01 00 00 C7 44 24 0C 00 01 00 00 E9 5B 8C 6B FF C7 44 24 04 00 01 00 00 C7 44 24",
                    "08 00 01 00 00 E9 C6 91 6B FF 3D 00 01 00 00 75 06 B8 00 00 E0 00 C3 55 89 E5 81 EC 04 05 00 00",
                    "E9 54 76 81 FF"),
                new(0x3000, PortraitMask.Bytes),
            ],
            Credit: St0ny),
        new(PatchIds.NameplateDistance, "Nameplate Distance", PatchGroup.Nameplates,
            "Nameplates show from farther away",
            "Shows nameplates farther away than the default 41 yards.",
            false, [], []),
        new(PatchIds.NameplateSort, "Nameplate Sorting", PatchGroup.Nameplates,
            "Nearer nameplates draw over farther ones, with your target on top",
            "Draws nearer nameplates over farther ones, with your target always on top.",
            false,
            [
                new("D9 E8 8D 46 74 50 68 10 58 49 00 57 6A 04", "D9 E8 8D 46 74 50 68 00 E3 DF 00 57 6A 04"),
                new("8B F1 8B 86 A8 02 00 00 3B 05 B0 07 BD 00 57 75 14",
                    "8B F1 57 EB 2B 02 00 00 3B 05 B0 07 BD 00 57 75 14"),
            ],
            [
                new(0x300,
                    "60 E8 06 00 00 00 61 E9 04 75 69 FF 80 3D 92 07 BD 00 00 0F 84 A8 00 00 00 E8 D2 55 60 FF 85 C0",
                    "0F 84 9B 00 00 00 68 00 E5 DF 00 89 C1 8B 10 FF 52 2C 83 25 24 E5 DF 00 00 6A 00 68 C2 E3 DF 00",
                    "E8 EB 67 6D FF 83 C4 08 B9 01 00 00 00 3B 0D 24 E5 DF 00 7D 4D 8B 04 CD 80 E5 DF 00 8B 14 CD 84",
                    "E5 DF 00 89 CB 85 DB 74 28 39 14 DD 7C E5 DF 00 7D 1F 8B 34 DD 78 E5 DF 00 89 34 DD 80 E5 DF 00",
                    "8B 34 DD 7C E5 DF 00 89 34 DD 84 E5 DF 00 4B EB D4 89 04 DD 80 E5 DF 00 89 14 DD 84 E5 DF 00 41",
                    "EB AB 31 DB 3B 1D 24 E5 DF 00 7D 15 6A 01 8D 43 0A 50 8B 0C DD 80 E5 DF 00 E8 E2 2C 69 FF 43 EB",
                    "E3 C3 6A 00 68 18 1F 9E 00 6A 08 FF 74 24 14 FF 74 24 14 E8 D8 69 6D FF 83 C4 14 85 C0 0F 84 96",
                    "00 00 00 8B 88 38 0C 00 00 85 C9 0F 84 88 00 00 00 8B 15 24 E5 DF 00 83 FA 40 7D 7D 89 0C D5 80",
                    "E5 DF 00 BA 00 00 80 BF 8B 4C 24 04 3B 0D B0 07 BD 00 75 0C 8B 4C 24 08 3B 0D B4 07 BD 00 74 46",
                    "68 10 E5 DF 00 89 C1 8B 10 FF 52 2C D9 05 10 E5 DF 00 D8 25 00 E5 DF 00 D8 C8 D9 05 14 E5 DF 00",
                    "D8 25 04 E5 DF 00 D8 C8 DE C1 D9 05 18 E5 DF 00 D8 25 08 E5 DF 00 D8 C8 DE C1 D9 1D 20 E5 DF 00",
                    "8B 15 20 E5 DF 00 8B 0D 24 E5 DF 00 89 14 CD 84 E5 DF 00 FF 05 24 E5 DF 00 B8 01 00 00 00 C3"),
            ]),
        new(PatchIds.NameplateStack, "Nameplate Stacking", PatchGroup.Nameplates,
            "Nameplates don't pile on top of each other",
            "Moves overlapping nameplates apart so they don't cover each other, gliding into place.",
            false,
            [
                new("6A 00 E8 B4 C7 11 00 56 E8 AE C7 22 00 83 C4 08", "6A 00 E8 B4 C7 11 00 56 E8 1E 57 90 00 83 C4 08"),
                new("74 05 8D 46 20 EB 02 33 C0 D9 45 F8 6A 01 83 EC 08",
                    "74 05 8D 46 20 EB 02 33 C0 E8 0B 8F 6D 00 83 EC 08"),
                new("83 C4 08 83 A6 10 0B 00 00 FE 8B 3D 4C 75 CD 00", "83 C4 08 E8 43 57 90 00 90 90 8B 3D 4C 75 CD 00"),
            ],
            [
                new(0x800,
                    "60 E8 3F 00 00 00 61 E9 84 70 92 FF 59 D9 45 F8 8B 15 40 EB DF 00 4A 78 10 39 1C 95 00 F0 DF 00",
                    "75 F4 D8 04 95 00 F5 DF 00 6A 01 FF E1 83 A6 10 0B 00 00 FE 83 3D 58 EB DF 00 00 74 07 83 8E 10",
                    "0B 00 00 01 C3 31 F6 8B 3D 88 AA AD 00 F7 C7 01 00 00 00 75 58 85 FF 74 54 83 FE 40 7D 4F 89 3C",
                    "B5 00 F0 DF 00 8B 87 E0 02 00 00 89 04 B5 00 F1 DF 00 8B 87 E4 02 00 00 89 04 B5 00 F2 DF 00 89",
                    "34 B5 00 F6 DF 00 8D 4F 20 8B 01 FF 50 28 D9 1C B5 00 F3 DF 00 8D 4F 20 8B 01 FF 50 2C D9 1C B5",
                    "00 F4 DF 00 46 8B BF A0 02 00 00 EB A0 89 35 40 EB DF 00 B9 01 00 00 00 39 F1 7D 3C 8B 04 8D 00",
                    "F6 DF 00 89 CB 85 DB 74 25 8B 14 9D FC F5 DF 00 D9 04 85 00 F2 DF 00 D9 04 95 00 F2 DF 00 DF F1",
                    "DD D8 76 0A 89 14 9D 00 F6 DF 00 4B EB D7 89 04 9D 00 F6 DF 00 41 EB C0 31 C9 39 F1 0F 8D C2 00",
                    "00 00 8B 04 8D 00 F6 DF 00 8B 14 85 00 F2 DF 00 89 15 44 EB DF 00 89 0D 4C EB DF 00 83 25 48 EB",
                    "DF 00 00 31 DB 39 CB 7D 73 8B 14 9D 00 F6 DF 00 D9 04 85 00 F3 DF 00 D8 04 95 00 F3 DF 00 D8 0D",
                    "10 EB DF 00 D9 04 85 00 F1 DF 00 D8 24 95 00 F1 DF 00 D9 E1 DF F1 DD D8 73 3F D9 04 85 00 F4 DF",
                    "00 D8 04 95 00 F4 DF 00 D8 0D 08 EB DF 00 D9 05 44 EB DF 00 D8 24 95 00 F5 DF 00 D9 E1 DF F1 73",
                    "16 D8 04 95 00 F5 DF 00 D9 1D 44 EB DF 00 83 0D 48 EB DF 00 01 EB 02 DD D8 43 EB 89 83 3D 48 EB",
                    "DF 00 00 74 0C FF 0D 4C EB DF 00 0F 89 6B FF FF FF 8B 15 44 EB DF 00 89 14 85 00 F5 DF 00 41 E9",
                    "36 FF FF FF E8 57 C4 A6 FF 89 C2 2B 15 54 EB DF 00 A3 54 EB DF 00 89 15 60 EB DF 00 DB 05 60 EB",
                    "DF 00 D8 0D 00 EB DF 00 D9 E8 DF F1 73 04 DD D8 D9 E8 D9 1D 5C EB DF 00 83 25 58 EB DF 00 00 31",
                    "C9 39 F1 7D 6D D9 04 8D 00 F5 DF 00 D8 24 8D 00 F2 DF 00 D9 1C 8D 00 F5 DF 00 8B 04 8D 00 F0 DF",
                    "00 8B 15 50 EB DF 00 4A 78 45 39 04 95 00 F7 DF 00 75 F4 D9 04 95 00 F8 DF 00 D9 04 8D 00 F5 DF",
                    "00 D8 E1 D9 C0 D9 E1 D9 05 04 EB DF 00 DF F1 DD D8 77 18 D8 0D 5C EB DF 00 DE C1 D9 1C 8D 00 F5",
                    "DF 00 83 0D 58 EB DF 00 01 EB 04 DD D8 DD D8 41 EB 8F 31 C9 39 F1 7D 1F 8B 04 8D 00 F0 DF 00 89",
                    "04 8D 00 F7 DF 00 8B 04 8D 00 F5 DF 00 89 04 8D 00 F8 DF 00 41 EB DD 89 35 50 EB DF 00 C3"),
                new(0xB00, "0A D7 23 3C 6F 12 03 3A"),
                new(0xB10, "00 00 00 3F"),
            ]),
        new(PatchIds.ShortNumbers, "Short Combat Numbers", PatchGroup.CombatText,
            "Big damage and healing numbers shortened (12.3K, 1.2M)",
            "Shows damage and healing numbers over targets from 10,000 up as 12.3K, 1.2M or 2B.",
            false,
            [
                new("56 68 9C 28 9E 00 8D 45 E0 6A 20 50 E8 9C CC 04 00",
                    "56 68 9C 28 9E 00 8D 45 E0 6A 20 50 E8 2C BC 6D 00"),
                new("50 68 9C 28 9E 00 8D 45 E0 6A 20 50 E8 B7 F7 05 00",
                    "50 68 9C 28 9E 00 8D 45 E0 6A 20 50 E8 47 E7 6E 00"),
                new("57 68 4C 3A A3 00 8D 55 E0 6A 20 52 E8 D7 CB 04 00",
                    "57 68 4C 3A A3 00 8D 55 E0 6A 20 52 E8 67 BC 6D 00"),
                new("50 68 4C 3A A3 00 8D 45 E0 6A 20 50 E8 57 F7 05 00",
                    "50 68 4C 3A A3 00 8D 45 E0 6A 20 50 E8 E7 E7 6E 00"),
            ],
            [
                new(0x000,
                    "8B 44 24 10 3D 10 27 00 00 7C 6E B9 98 E0 DF 00 BA 80 E0 DF 00 3D 0E 42 0F 00 7C 1B B9 A0 E0 DF",
                    "00 BA 88 E0 DF 00 3D B0 06 9A 3B 7C 0A B9 A8 E0 DF 00 BA 90 E0 DF 00 DB 44 24 10 DC 32 83 EC 08",
                    "DD 1C 24 51 FF 74 24 14 FF 74 24 14 E8 1F 10 97 FF 83 C4 14 8B 4C 24 04 8A 11 84 D2 74 1A 80 FA",
                    "2E 74 03 41 EB F2 80 79 01 30 75 0C 8A 51 02 88 11 C6 41 01 00 83 E8 02 C3 E9 F2 0F 97 FF CC CC",
                    "00 00 00 00 00 40 8F 40 00 00 00 00 80 84 2E 41 00 00 00 00 65 CD CD 41 25 2E 31 66 4B 00 00 00",
                    "25 2E 31 66 4D 00 00 00 25 2E 31 66 42 00 00 00"),
                new(0x100,
                    "8B 44 24 10 3D 10 27 00 00 7C 31 B9 48 E1 DF 00 BA 80 E0 DF 00 3D 0E 42 0F 00 7C 1B B9 50 E1 DF",
                    "00 BA 88 E0 DF 00 3D B0 06 9A 3B 7C 0A B9 58 E1 DF 00 BA 90 E0 DF 00 E9 FB FE FF FF E9 2F 0F 97",
                    "FF CC CC CC CC CC CC CC 2B 25 2E 31 66 4B 00 00 2B 25 2E 31 66 4D 00 00 2B 25 2E 31 66 42 00 00"),
            ]),
        new(PatchIds.DamageColors, "Damage Number Colors", PatchGroup.CombatText,
            "Big hits shown in color",
            "Colors damage numbers over targets by damage amount.",
            false,
            [
                new("83 E1 02 51 52 E8 03 3C 0C 00", "83 E1 02 51 52 E8 D3 BD 6D 00"),
                new("51 6A 08 52 E8 5E 67 0D 00", "51 6A 08 52 E8 33 E9 6E 00"),
            ],
            [
                new(0x200,
                    "8B 45 0C EB 03 8B 45 08 B9 40 E2 DF 00 8B 11 85 D2 74 10 39 D0 7D 05 83 C1 08 EB F1 83 C1 04 89",
                    "4C 24 10 E9 08 7E 9E FF"),
            ]),
        new(PatchIds.GroundTargets, "Ground Target Macros", PatchGroup.QualityOfLife,
            "Macros can drop ground spells at your cursor, on yourself or on your target",
            "Adds [@cursor], [@playerlocation] and [@targetlocation] (or the short [@cur], [@ploc] and [@tloc]) to /cast "
                + "macros for spells you place on the ground, like Flamestrike, Flare or Death and Decay: no targeting circle, "
                + "and the game's usual range and placement rules apply. Only works with patches loaded in memory.",
            true, [], [], MemoryOnly: true),
    ];

    // Everything except the two exe patches is applied in memory by SynastriaLauncher.dll at startup, from its own C++
    // definitions of these patches (native/SynastriaLauncher). The routines here are the reference the tests check the
    // DLL against, and let the launcher recognize an exe patched on disk by an older patcher.
    public static bool InMemory(string id) => id is not (PatchIds.LargeAddressAware or PatchIds.DllLoader);

    // The loader's call to load SynastriaLauncher.dll, which SynastriaPatcher.ps1's loader never had.
    internal static readonly (int Offset, int Length)[] PatcherLoadRanges = [(0x1930, 6), (0x19C0, 0x36)];

    public static PatchDefinition Get(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unknown patch: {id}", nameof(id));
}
