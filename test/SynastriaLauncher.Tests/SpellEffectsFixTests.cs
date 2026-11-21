// Covers the Spell Particle Effects fix: MPQ and client table round trips, moving Reforged's added visual rows into
// unused stock IDs with their references, and keeping patch-V.mpq in line with patch-P.mpq. The real-client check
// skips unless SYNASTRIA_TEST_GAME points at a game folder with patch-P.mpq installed.

using SynastriaLauncher.Core.Mpq;
using SynastriaLauncher.Core.Reforged;
using static SynastriaLauncher.Core.Reforged.SpellEffectsFix;

namespace SynastriaLauncher.Tests;

public sealed class SpellEffectsFixTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-spellfix-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly Dictionary<string, int> FieldCounts = new() { [Visual] = 32, [Kit] = 38, [Effect] = 7, [Attach] = 10 };

    private static uint[] Row(string table, uint id, params (int Field, uint Value)[] values)
    {
        var row = new uint[FieldCounts[table]];
        row[0] = id;
        foreach (var (field, value) in values)
        {
            row[field] = value;
        }
        return row;
    }

    private static DbcTable Table(string table, params uint[][] rows) => new(FieldCounts[table], [.. rows], [0, (byte)'a', 0]);

    private static Dictionary<string, DbcTable> Stock() => new()
    {
        [Visual] = Table(Visual, Row(Visual, 1, (2, 1)), Row(Visual, 5)),
        [Kit] = Table(Kit, Row(Kit, 1), Row(Kit, 2), Row(Kit, 3), Row(Kit, 5), Row(Kit, 10)),
        [Effect] = Table(Effect, Row(Effect, 1), Row(Effect, 3)),
        [Attach] = Table(Attach, Row(Attach, 1, (1, 1)), Row(Attach, 4, (1, 2))),
    };

    // Reforged's version: visual 1 now casts new kit 11, new visual 6 casts new kit 12, which uses new effect 4, and
    // new model attach 9 hangs effect 4 on kit 12.
    private static Dictionary<string, DbcTable> Reforged() => new()
    {
        [Visual] = Table(Visual, Row(Visual, 1, (2, 11)), Row(Visual, 5), Row(Visual, 6, (2, 12), (8, 4))),
        [Kit] = Table(Kit, Row(Kit, 1), Row(Kit, 2), Row(Kit, 3), Row(Kit, 5), Row(Kit, 10), Row(Kit, 11, (5, 1)), Row(Kit, 12, (6, 4))),
        [Effect] = Table(Effect, Row(Effect, 1), Row(Effect, 3), Row(Effect, 4)),
        [Attach] = Table(Attach, Row(Attach, 1, (1, 1)), Row(Attach, 4, (1, 2)), Row(Attach, 9, (1, 12), (2, 4))),
    };

    private static uint[] Get(DbcTable table, uint id) => table.Rows.Single(r => r[0] == id);

    // An archive written with several files, one spanning several sectors, reads back byte for byte.
    [Fact]
    public void MpqRoundTrip()
    {
        var random = new byte[10_000];
        new Random(7).NextBytes(random);
        var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("spell visual ", 2000)));
        var path = Path.Combine(_root, "test.mpq");
        File.WriteAllBytes(path, MpqWriter.Write([("DBFilesClient\\A.dbc", random), ("Dir\\Text.txt", text), ("Empty.txt", [])]));
        using var archive = new MpqArchive(path);
        Assert.Equal(random, archive.Read("DBFilesClient\\A.dbc"));
        Assert.Equal(text, archive.Read("dir/text.txt"));
        Assert.Empty(archive.Read("Empty.txt")!);
        Assert.True(archive.Contains("(listfile)"));
        Assert.Null(archive.Read("Missing.txt"));
    }

    // A file that isn't an archive is refused.
    [Fact]
    public void RefusesANonArchive()
    {
        var path = Path.Combine(_root, "junk.mpq");
        File.WriteAllBytes(path, new byte[64]);
        Assert.Throws<InvalidDataException>(() => new MpqArchive(path));
    }

    // A client table writes out the same bytes it was read from.
    [Fact]
    public void DbcRoundTrip()
    {
        var bytes = Table(Kit, Row(Kit, 3, (5, 7)), Row(Kit, 1)).Write();
        Assert.Equal(bytes, DbcTable.Read(bytes).Write());
        Assert.Throws<InvalidDataException>(() => DbcTable.Read([1, 2, 3]));
    }

    // Rows added past each stock maximum move to the lowest unused IDs, and every reference follows them.
    [Fact]
    public void MovesAddedRowsAndReferences()
    {
        var result = Fix(Reforged(), Stock());
        Assert.Equal([1u, 2, 5], result[Visual].Rows.Select(r => r[0]));
        Assert.Equal(4u, Get(result[Visual], 1)[2]);
        Assert.Equal(6u, Get(result[Visual], 2)[2]);
        Assert.Equal(2u, Get(result[Visual], 2)[8]);
        Assert.Equal([1u, 2, 3, 4, 5, 6, 10], result[Kit].Rows.Select(r => r[0]));
        Assert.Equal(1u, Get(result[Kit], 4)[5]);
        Assert.Equal(2u, Get(result[Kit], 6)[6]);
        Assert.Equal([1u, 2, 3], result[Effect].Rows.Select(r => r[0]));
        Assert.Equal(Row(Attach, 2, (1, 6), (2, 2)), Get(result[Attach], 2));
        Assert.Equal(Row(Visual, 5), Get(result[Visual], 5));
        Assert.Equal(Reforged()[Kit].Strings, result[Kit].Strings);
    }

    // An unused ID that a row or a spell already points at stays unused, so nothing starts showing a moved row.
    [Fact]
    public void SkipsIdsSomethingPointsAt()
    {
        var stock = Stock();
        stock[Kit].Rows[0][4] = 2;
        stock[Effect] = Table(Effect, Row(Effect, 1), Row(Effect, 3), Row(Effect, 5));
        var reforged = Reforged();
        reforged[Effect] = Table(Effect, Row(Effect, 1), Row(Effect, 3), Row(Effect, 5), Row(Effect, 6));
        reforged[Kit].Rows[^1][6] = 6;
        reforged[Visual].Rows[^1][8] = 6;
        reforged[Attach].Rows[^1][2] = 6;
        var result = Fix(reforged, stock, usedVisuals: [2]);
        Assert.Equal([1u, 3, 4, 5], result[Effect].Rows.Select(r => r[0]));
        Assert.Equal([1u, 3, 5], result[Visual].Rows.Select(r => r[0]));
        Assert.Equal(4u, Get(result[Kit], 6)[6]);
    }

    // Tables that add nothing past the stock IDs need no fix.
    [Fact]
    public void NothingAddedNeedsNoFix() => Assert.Empty(Fix(Stock(), Stock()));

    // When the stock range has no room for the added rows, the fix refuses instead of reusing IDs.
    [Fact]
    public void RefusesWhenThereIsNoRoom()
    {
        var reforged = Reforged();
        reforged[Effect] = Table(Effect, Row(Effect, 1), Row(Effect, 3), Row(Effect, 4), Row(Effect, 5));
        var stock = Stock();
        stock[Effect] = Table(Effect, Row(Effect, 1), Row(Effect, 2), Row(Effect, 3));
        Assert.Throws<InvalidDataException>(() => Fix(reforged, stock));
    }

    private string MakeGame(bool withReforged)
    {
        var game = Path.Combine(_root, "game");
        var data = Directory.CreateDirectory(Path.Combine(game, "Data", "enUS")).Parent!.FullName;
        var spells = new DbcTable(133, [new uint[133]], [0]);
        spells.Rows[0][131] = 1;
        File.WriteAllBytes(Path.Combine(data, "enUS", "locale-enUS.MPQ"),
            MpqWriter.Write(Stock().Select(t => (DbcPath(t.Key), t.Value.Write())).Append((DbcPath("Spell"), spells.Write())).ToList()));
        if (withReforged)
        {
            File.WriteAllBytes(Path.Combine(data, SourceName), MpqWriter.Write(Reforged().Select(t => (DbcPath(t.Key), t.Value.Write())).ToList()));
        }
        return game;
    }

    // With patch-P.mpq installed, patch-V.mpq is written once, holds the fixed tables, and is then left as it is.
    [Fact]
    public void SyncWritesTheFixOnce()
    {
        var game = MakeGame(withReforged: true);
        Assert.Equal(SpellEffectsFixState.Written, Sync(game));
        var path = Path.Combine(game, "Data", FileName);
        using (var archive = new MpqArchive(path))
        {
            Assert.True(archive.Contains(MarkerName));
            Assert.Equal(Fix(Reforged(), Stock(), [1])[Kit].Write(), archive.Read(DbcPath(Kit)));
        }
        Assert.Equal(SpellEffectsFixState.Current, Sync(game));
    }

    // A patch-P.mpq replaced after the fix was made (a Reforged update) gets a rebuilt fix.
    [Fact]
    public void SyncRebuildsAfterReforgedChanges()
    {
        var game = MakeGame(withReforged: true);
        Sync(game);
        var source = Path.Combine(game, "Data", SourceName);
        var reforged = Reforged();
        reforged[Kit].Rows.Add(Row(Kit, 13));
        File.WriteAllBytes(source, MpqWriter.Write(reforged.Select(t => (DbcPath(t.Key), t.Value.Write())).ToList()));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(SpellEffectsFixState.Written, Sync(game));
        using var archive = new MpqArchive(Path.Combine(game, "Data", FileName));
        Assert.Equal(8, DbcTable.Read(archive.Read(DbcPath(Kit))!).Rows.Count);
    }

    // A fix made by an older launcher (another marker) is rebuilt even when it's newer than patch-P.mpq.
    [Fact]
    public void SyncRebuildsAnOlderFix()
    {
        var game = MakeGame(withReforged: true);
        var path = Path.Combine(game, "Data", FileName);
        File.WriteAllBytes(path, MpqWriter.Write([(MarkerName, "Fix version 0\r\n"u8.ToArray())]));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(SpellEffectsFixState.Written, Sync(game));
        using var archive = new MpqArchive(path);
        Assert.True(archive.Contains(DbcPath(Kit)));
    }

    // Once patch-P.mpq is gone, the launcher's patch-V.mpq goes too.
    [Fact]
    public void SyncRemovesTheFixWithReforged()
    {
        var game = MakeGame(withReforged: true);
        Sync(game);
        File.Delete(Path.Combine(game, "Data", SourceName));
        Assert.Equal(SpellEffectsFixState.Removed, Sync(game));
        Assert.False(File.Exists(Path.Combine(game, "Data", FileName)));
        Assert.Equal(SpellEffectsFixState.NotNeeded, Sync(game));
    }

    // A patch-V.mpq from something else is never overwritten or removed.
    [Fact]
    public void SyncLeavesSomeoneElsesPatchZ()
    {
        var game = MakeGame(withReforged: true);
        var path = Path.Combine(game, "Data", FileName);
        var other = MpqWriter.Write([("Interface\\Other.txt", [1, 2, 3])]);
        File.WriteAllBytes(path, other);
        Assert.Equal(SpellEffectsFixState.Blocked, Sync(game));
        File.Delete(Path.Combine(game, "Data", SourceName));
        Assert.Equal(SpellEffectsFixState.Blocked, Sync(game));
        Assert.Equal(other, File.ReadAllBytes(path));
    }

    // On a real client, no fixed row sits past the stock maximum, and every row Reforged added is still reachable
    // with its contents unchanged apart from the moved IDs.
    [Fact]
    public void FixesTheRealClient()
    {
        var game = Environment.GetEnvironmentVariable("SYNASTRIA_TEST_GAME");
        Assert.SkipWhen(game is null || !File.Exists(Path.Combine(game, "Data", SourceName)), "Set SYNASTRIA_TEST_GAME to a game folder with patch-P.mpq");
        var bytes = Build(game!);
        Assert.NotNull(bytes);
        var path = Path.Combine(_root, FileName);
        File.WriteAllBytes(path, bytes);
        using var archive = new MpqArchive(path);
        using var source = new MpqArchive(Path.Combine(game!, "Data", SourceName));
        var stock = new Dictionary<string, DbcTable>();
        foreach (var archivePath in StockArchives(game!).Reverse())
        {
            using var stockArchive = new MpqArchive(archivePath);
            foreach (var name in References.Keys)
            {
                if (stockArchive.Read(DbcPath(name)) is { } table)
                {
                    stock[name] = DbcTable.Read(table);
                }
            }
        }
        foreach (var name in References.Keys)
        {
            var fixedTable = DbcTable.Read(archive.Read(DbcPath(name))!);
            var original = DbcTable.Read(source.Read(DbcPath(name))!);
            Assert.Equal(original.Rows.Count, fixedTable.Rows.Count);
            Assert.True(fixedTable.Rows.Max(r => r[0]) <= stock[name].Rows.Max(r => r[0]), name);
            Assert.Equal(fixedTable.Rows.Count, fixedTable.Rows.Select(r => r[0]).Distinct().Count());
        }
    }
}
