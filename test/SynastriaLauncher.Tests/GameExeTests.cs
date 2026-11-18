// Covers patching a real WowExt.exe through GameExe: availability, applying, settings, re-applying and restoring.
// Skips unless SYNASTRIA_TEST_WOWEXT points at an unpatched exe.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class GameExeTests
{
    // Every patch that can be written on disk fits the unpatched exe, and none reports itself applied except Large
    // Address Aware. Memory-only patches never fit on disk.
    [Fact]
    public void AllPatchesFitOriginal()
    {
        var bytes = TestExe.OriginalBytes();
        Assert.Equal(PatchCatalog.All.Where(p => !p.MemoryOnly).Select(p => p.Id), ExePatcher.AvailableIds(bytes));
        Assert.All(PatchCatalog.All.Where(p => p.MemoryOnly),
            p => Assert.Equal(PatchStatus.Unavailable, ExePatcher.Plan(bytes, p, PatchSettings.Keep).Status));
        var applied = PatchCatalog.All
            .Where(p => ExePatcher.Plan(bytes, p, PatchSettings.Keep).Status == PatchStatus.Applied)
            .Select(p => p.Id);
        Assert.Equal([PatchIds.LargeAddressAware], applied);
    }

    // Applying everything backs up the original once, makes every patch report applied, writes the default
    // settings and creates the DLL folder.
    [Fact]
    public void ApplyAllThenEverythingIsApplied()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        var result = exe.Apply(ExePatcher.AvailableIds(original), PatchSettings.Keep);
        Assert.True(result.HasChanges);
        Assert.Equal(original, File.ReadAllBytes(exe.BackupPath));
        var patched = exe.Read();
        Assert.All(PatchCatalog.All.Where(p => !p.MemoryOnly),
            p => Assert.Equal(PatchStatus.Applied, ExePatcher.Plan(patched, p, PatchSettings.Keep).Status));
        var settings = ExePatcher.ReadSettings(patched);
        Assert.Equal(60, settings.NameplateDistance);
        Assert.Equal(100, settings.StackSpacing);
        Assert.Equal(DamageColors.Default, settings.DamageColors);
        Assert.Equal("DLLs", settings.DllFolder);
        Assert.True(Directory.Exists(Path.Combine(game.Folder, "DLLs")));
    }

    // Applying the same patches again changes nothing and leaves the exe untouched.
    [Fact]
    public void ReapplyChangesNothing()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.Apply(ExePatcher.AvailableIds(original), PatchSettings.Keep);
        var once = exe.Read();
        var again = exe.Apply(ExePatcher.AvailableIds(original), PatchSettings.Keep);
        Assert.False(again.HasChanges);
        Assert.Equal(once, exe.Read());
    }

    // A new setting on an applied patch is one pending change, and once written it reads back.
    [Fact]
    public void ChangedSettingsApply()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.Apply(ExePatcher.AvailableIds(original), PatchSettings.Keep);
        var colors = DamageColors.Parse("100K=00FF00,2.5M=FF0000");
        var settings = new PatchSettings(80, 70, colors, @"Mods\Native");
        string[] ids = [PatchIds.NameplateDistance, PatchIds.NameplateStack, PatchIds.DamageColors, PatchIds.DllLoader];
        Assert.All(ids, id => Assert.Equal(PatchStatus.NotApplied, ExePatcher.Plan(exe.Read(), id, settings).Status));
        Assert.Equal(4, exe.Apply(ids, settings).Changed.Count);
        var current = ExePatcher.ReadSettings(exe.Read());
        Assert.Equal(80, current.NameplateDistance);
        Assert.Equal(70, current.StackSpacing);
        Assert.Equal(colors, current.DamageColors);
        Assert.Equal(@"Mods\Native", current.DllFolder);
        Assert.True(Directory.Exists(Path.Combine(game.Folder, "Mods", "Native")));
    }

    // Keeping settings after a custom value leaves the custom value in place.
    [Fact]
    public void KeepLeavesCustomDistance()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.Apply([PatchIds.NameplateDistance], new PatchSettings(NameplateDistance: 80));
        Assert.False(exe.Apply([PatchIds.NameplateDistance], PatchSettings.Keep).HasChanges);
        Assert.Equal(80, ExePatcher.ReadSettings(exe.Read()).NameplateDistance);
    }

    // Switching a patch off rebuilds from the original with the rest still on, which equals applying only those.
    [Fact]
    public void SwitchingOffRebuildsFromOriginal()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        string[] all = [PatchIds.NoAutoAfk, PatchIds.HdPortraits, PatchIds.NameplateDistance];
        exe.ApplyState(all, new PatchSettings(NameplateDistance: 70));
        Assert.Equal(all.Order(), GameExe.AppliedIds(exe.Read()).Order());
        var result = exe.ApplyState([PatchIds.NoAutoAfk, PatchIds.NameplateDistance], new PatchSettings(NameplateDistance: 70));
        Assert.True(result.Written);
        Assert.True(result.FromOriginal);
        var expected = ExePatcher.Apply(original, [PatchIds.NoAutoAfk, PatchIds.NameplateDistance, PatchIds.LargeAddressAware],
            new PatchSettings(NameplateDistance: 70)).Bytes;
        Assert.Equal(expected, exe.Read());
    }

    // Applying the same switches and settings again writes nothing.
    [Fact]
    public void SameStateWritesNothing()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        var settings = new PatchSettings(60, 100, DamageColors.Default, "DLLs");
        exe.ApplyState(ExePatcher.AvailableIds(original), settings);
        Assert.False(exe.ApplyState(GameExe.AppliedIds(exe.Read()), settings).Written);
    }

    // Restoring puts the original exe back.
    [Fact]
    public void RestoreReturnsOriginal()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.Apply(ExePatcher.AvailableIds(original), PatchSettings.Keep);
        exe.Restore();
        Assert.Equal(original, exe.Read());
    }
}
