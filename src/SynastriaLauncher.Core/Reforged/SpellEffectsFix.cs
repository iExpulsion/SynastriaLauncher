using System.Text;
using SynastriaLauncher.Core.Mpq;
using SynastriaLauncher.Core.Wdm;

namespace SynastriaLauncher.Core.Reforged;

public enum SpellEffectsFixState
{
    NotNeeded,
    Current,
    Written,
    Removed,
    Blocked,
}

// Spell Particle Effects (patch-P.mpq) adds spell visual rows past the stock client's highest IDs, the same IDs the
// game client gives the visual rows it adds while running, so both claim them and spells lose their effects. The fix
// is patch-Z.mpq, which loads after Reforged's patches: Reforged's visual tables with every row it added past the
// stock IDs moved to an unused ID within the stock range, and every reference to those rows changed to match. It's
// built from the player's own patch-P.mpq, so nothing of Reforged's is re-hosted.
public static class SpellEffectsFix
{
    public const string FileName = "patch-Z.mpq";
    public const string SourceName = "patch-P.mpq";
    public const string MarkerName = "SynastriaLauncher\\SpellEffectsFix.txt";

    public const string Visual = "SpellVisual";
    public const string Kit = "SpellVisualKit";
    public const string Effect = "SpellVisualEffectName";
    public const string Attach = "SpellVisualKitModelAttach";

    // Each table's fields that hold another visual table's ID.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> References { get; } =
        new Dictionary<string, IReadOnlyDictionary<int, string>>
        {
            [Visual] = Fields(Kit, 1, 2, 3, 4, 5, 6, 14, 15, 22, 23, 24, 25).Append(new(8, Effect)).ToDictionary(),
            [Kit] = Fields(Effect, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14).ToDictionary(),
            [Effect] = new Dictionary<int, string>(),
            [Attach] = new Dictionary<int, string> { [1] = Kit, [2] = Effect },
        };

    private static IEnumerable<KeyValuePair<int, string>> Fields(string table, params int[] fields) =>
        fields.Select(f => new KeyValuePair<int, string>(f, table));

    public static string DbcPath(string table) => $"DBFilesClient\\{table}.dbc";

    private static readonly byte[] Marker = Encoding.ASCII.GetBytes(
        "Made by the Synastria Launcher from Project Reforged's patch-P.mpq. The launcher rebuilds or removes it as needed.\r\n"
        + "Fix version 1\r\n");

    // Spell.dbc's two visual fields.
    public static readonly int[] SpellVisualFields = [131, 132];

    // IDs some row points at, whether or not that row exists: a stock kit naming a missing effect must not start
    // showing a moved one.
    private static IEnumerable<uint> PointedAt(string target, IEnumerable<IReadOnlyDictionary<string, DbcTable>> sets) =>
        sets.SelectMany(set => set).SelectMany(t => References[t.Key].Where(r => r.Value == target)
            .SelectMany(r => t.Value.Rows.Select(row => row[r.Key])));

    // The tables with Reforged's rows past each stock table's highest ID moved to IDs below it that nothing has or
    // points at. usedVisuals are the visual IDs spells use. Tables only come back when something moved; an empty
    // result means Reforged's tables need no fix.
    public static IReadOnlyDictionary<string, DbcTable> Fix(IReadOnlyDictionary<string, DbcTable> reforged,
        IReadOnlyDictionary<string, DbcTable> stock, IEnumerable<uint>? usedVisuals = null)
    {
        var moves = new Dictionary<string, Dictionary<uint, uint>>();
        foreach (var (name, table) in reforged)
        {
            var stockIds = stock[name].Rows.Select(r => r[0]).ToHashSet();
            var taken = table.Rows.Select(r => r[0]).Concat(stockIds).Concat(PointedAt(name, [reforged, stock]))
                .Concat(name == Visual ? usedVisuals ?? [] : []).ToHashSet();
            var highest = stockIds.Max();
            var added = table.Rows.Select(r => r[0]).Where(id => id > highest).Order().ToList();
            var free = Enumerable.Range(1, (int)highest).Select(i => (uint)i).Where(id => !taken.Contains(id)).Take(added.Count).ToList();
            moves[name] = free.Count == added.Count
                ? added.Zip(free).ToDictionary(p => p.First, p => p.Second)
                : throw new InvalidDataException($"{name} has no room for Project Reforged's {added.Count} new rows.");
        }
        var fixedTables = new Dictionary<string, DbcTable>();
        if (moves.Values.Any(m => m.Count > 0))
        {
            foreach (var (name, table) in reforged)
            {
                var references = References[name].Where(r => moves.ContainsKey(r.Value)).ToList();
                var rows = table.Rows.Select(row =>
                {
                    var copy = (uint[])row.Clone();
                    copy[0] = moves[name].GetValueOrDefault(row[0], row[0]);
                    foreach (var (field, target) in references)
                    {
                        copy[field] = moves[target].GetValueOrDefault(row[field], row[field]);
                    }
                    return copy;
                }).OrderBy(r => r[0]).ToList();
                fixedTables[name] = new DbcTable(table.FieldCount, rows, table.Strings);
            }
        }
        return fixedTables;
    }

    // The archives the stock client reads its tables from, highest priority first.
    public static IEnumerable<string> StockArchives(string gameFolder)
    {
        var data = ReforgedService.DataFolder(gameFolder);
        var locale = WdmCatalog.Locale(gameFolder);
        return new[]
        {
            Path.Combine(locale, $"patch-{locale}-4.MPQ"), "patch-4.MPQ",
            Path.Combine(locale, $"patch-{locale}-3.MPQ"), "patch-3.MPQ",
            Path.Combine(locale, $"patch-{locale}-2.MPQ"), "patch-2.MPQ",
            Path.Combine(locale, $"patch-{locale}.MPQ"), "patch.MPQ",
            Path.Combine(locale, $"locale-{locale}.MPQ"),
        }.Select(p => Path.Combine(data, p)).Where(File.Exists);
    }

    // The fix archive's bytes, or null when Reforged's tables need no fix.
    public static byte[]? Build(string gameFolder)
    {
        var data = ReforgedService.DataFolder(gameFolder);
        var reforged = new Dictionary<string, DbcTable>();
        using (var source = new MpqArchive(Path.Combine(data, SourceName)))
        {
            foreach (var name in References.Keys)
            {
                if (source.Read(DbcPath(name)) is { } bytes)
                {
                    reforged[name] = DbcTable.Read(bytes);
                }
            }
        }
        var stock = new Dictionary<string, DbcTable>();
        byte[]? spells = null;
        foreach (var path in StockArchives(gameFolder))
        {
            using var archive = new MpqArchive(path);
            foreach (var name in reforged.Keys.Where(n => !stock.ContainsKey(n)))
            {
                if (archive.Read(DbcPath(name)) is { } bytes)
                {
                    stock[name] = DbcTable.Read(bytes);
                }
            }
            spells ??= archive.Read(DbcPath("Spell"));
        }
        var missing = reforged.Keys.Where(n => !stock.ContainsKey(n)).Concat(spells is null ? ["Spell"] : []).ToList();
        var fixedTables = missing.Count == 0 ? Fix(reforged, stock, SpellVisualFields.SelectMany(f => DbcTable.Column(spells!, f)))
            : throw new InvalidDataException($"The game's own {string.Join(", ", missing)} couldn't be found in its Data folder.");
        return fixedTables.Count == 0 ? null
            : MpqWriter.Write(fixedTables.Select(t => (DbcPath(t.Key), t.Value.Write())).Append((MarkerName, Marker)).ToList());
    }

    // The launcher's marker file in a patch-Z.mpq, or null when it isn't the launcher's.
    private static byte[]? ReadMarker(string path)
    {
        byte[]? marker = null;
        try
        {
            using var archive = new MpqArchive(path);
            marker = archive.Read(MarkerName);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            marker = null;
        }
        return marker;
    }

    public static bool IsOurs(string path) => ReadMarker(path) is not null;

    private static DateTime WrittenUtc(string path) =>
        (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? new FileInfo(path)).LastWriteTimeUtc;

    // Brings patch-Z.mpq in line with patch-P.mpq: built or rebuilt while Spell Particle Effects is installed and
    // needs it, removed otherwise. A patch-Z.mpq the launcher didn't make is never touched. A fix newer than
    // patch-P.mpq and made by this version of the launcher is taken as current without rebuilding it.
    public static SpellEffectsFixState Sync(string gameFolder)
    {
        var data = ReforgedService.DataFolder(gameFolder);
        var target = Path.Combine(data, FileName);
        var source = Path.Combine(data, SourceName);
        var exists = File.Exists(target);
        var marker = exists ? ReadMarker(target) : null;
        var ours = marker is not null;
        var hasSource = File.Exists(source);
        var unchanged = ours && hasSource && marker.AsSpan().SequenceEqual(Marker) && File.GetLastWriteTimeUtc(target) >= WrittenUtc(source);
        var built = hasSource && !unchanged && (!exists || ours) ? Build(gameFolder) : null;
        SpellEffectsFixState state;
        if (exists && !ours)
        {
            state = SpellEffectsFixState.Blocked;
        }
        else if (unchanged || built is not null && ours && File.ReadAllBytes(target).AsSpan().SequenceEqual(built))
        {
            state = SpellEffectsFixState.Current;
        }
        else if (built is not null)
        {
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, built);
            File.Move(temp, target, overwrite: true);
            state = SpellEffectsFixState.Written;
        }
        else if (ours)
        {
            File.Delete(target);
            state = SpellEffectsFixState.Removed;
        }
        else
        {
            state = SpellEffectsFixState.NotNeeded;
        }
        return state;
    }
}
