using System.Globalization;
using System.Reflection;
using System.Text;

namespace SynastriaLauncher.Core.Patching;

public enum PatcherOutcome
{
    Applied,
    AlreadyOn,
    Skipped,
}

public sealed record PatcherResult(PatcherOutcome Outcome, string Detail);

// What SynastriaLauncher.dll reported the last time the game started.
public sealed record PatcherRun(DateTime? Time, IReadOnlyDictionary<string, PatcherResult> Patches);

// The in-memory patches: which are on and their settings, as SynastriaLauncher.ini in the game folder. The DLL reads
// it when the game starts.
public sealed record PatcherConfig(
    IReadOnlyList<string> On,
    int NameplateDistance,
    int StackSpacing,
    IReadOnlyList<DamageColorTier> DamageColors)
{
    public const string DllName = "SynastriaLauncher.dll";
    public const string IniName = "SynastriaLauncher.ini";
    public const string StatusName = "SynastriaLauncher.status.ini";
    private const string ResourceName = "SynastriaLauncher.Core.Patching.SynastriaLauncher.dll";

    public static string[] FileNames { get; } = [DllName, IniName, StatusName];

    // The DLL built into this launcher, or null in a build made without it (tools/build-patcher.ps1).
    public static byte[]? EmbeddedDll()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        byte[]? bytes = null;
        if (stream is not null)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        return bytes;
    }

    public static string FormatTiers(IEnumerable<DamageColorTier> tiers) =>
        string.Join(",", tiers.OrderByDescending(t => t.Amount).Select(t => $"{t.Amount}={t.Rgb:X6}"));

    public string ToIni()
    {
        var text = new StringBuilder();
        text.Append("; Written by Synastria Launcher. SynastriaLauncher.dll reads this when the game starts.\n\n[Patches]\n");
        foreach (var patch in PatchCatalog.All.Where(p => PatchCatalog.InMemory(p.Id)))
        {
            text.Append($"{patch.Id}={(On.Contains(patch.Id) ? 1 : 0)}\n");
        }
        text.Append("\n[Settings]\n");
        text.Append($"NameplateDistance={NameplateDistance}\n");
        text.Append($"StackSpacing={StackSpacing}\n");
        text.Append($"DamageColors={FormatTiers(DamageColors)}\n");
        return text.ToString();
    }

    public static PatcherConfig? FromIni(string text)
    {
        var values = ReadIni(text);
        PatcherConfig? config = null;
        if (values.ContainsKey("Patches"))
        {
            var patches = values["Patches"];
            var settings = values.GetValueOrDefault("Settings") ?? [];
            var on = PatchCatalog.All
                .Where(p => PatchCatalog.InMemory(p.Id) && patches.GetValueOrDefault(p.Id) == "1")
                .Select(p => p.Id)
                .ToList();
            config = new PatcherConfig(
                on,
                Number(settings, "NameplateDistance", PatchSettings.DefaultNameplateDistance),
                Number(settings, "StackSpacing", PatchSettings.DefaultStackSpacing),
                ParseTiers(settings.GetValueOrDefault("DamageColors")) ?? Patching.DamageColors.Default);
        }
        return config;
    }

    public static PatcherRun? ReadRun(string text)
    {
        var values = ReadIni(text);
        PatcherRun? run = null;
        if (values.TryGetValue("Patches", out var patches))
        {
            var time = values.GetValueOrDefault("Run")?.GetValueOrDefault("Time");
            var results = new Dictionary<string, PatcherResult>();
            foreach (var (id, value) in patches)
            {
                var split = value.IndexOf(':');
                var name = split < 0 ? value : value[..split];
                var detail = split < 0 ? "" : value[(split + 1)..].Trim();
                if (Enum.TryParse<PatcherOutcome>(name, out var outcome))
                {
                    results[id] = new PatcherResult(outcome, detail);
                }
            }
            run = new PatcherRun(
                DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null,
                results);
        }
        return run;
    }

    private static int Number(Dictionary<string, string> values, string key, int fallback) =>
        int.TryParse(values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static IReadOnlyList<DamageColorTier>? ParseTiers(string? text)
    {
        // Like the DLL: one bad item makes the whole line unusable, so the page shows what the game will do.
        var tiers = new List<DamageColorTier>();
        var items = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in items)
        {
            var parts = item.Split('=');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) && amount > 0
                && parts[1].Length == 6 && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                tiers.Add(new DamageColorTier(amount, rgb));
            }
        }
        return tiers.Count == items.Length && tiers.Count is > 0 and <= Patching.DamageColors.MaxTiers
            ? tiers.OrderByDescending(t => t.Amount).ToList() : null;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line[1..^1].Trim()] = current;
            }
            else if (current is not null && equals > 0 && !line.StartsWith(';'))
            {
                current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }
        return sections;
    }
}
