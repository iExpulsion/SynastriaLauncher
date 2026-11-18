// Covers the in-memory patcher (SynastriaLauncher.dll): its C++ patch definitions target the same code as PatchCatalog
// (same patterns, same byte swaps, same portrait mask and setting defaults), the byte-swap patches give the same mapped
// image as the on-disk patches, the code patches change nothing outside their sites, it refuses patches that don't
// match, its unit tests pass, and its settings and status files read back. The exe tests skip unless
// SYNASTRIA_TEST_WOWEXT is set and tools/build-patcher.ps1 has been run.

using System.Diagnostics;
using System.Globalization;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class MemoryPatcherTests
{
    private static readonly string[] Base = [PatchIds.LargeAddressAware, PatchIds.DllLoader];

    // Patches whose memory edits are the same bytes the on-disk patcher writes.
    private static readonly string[] ByteIdentical =
        [PatchIds.InterfaceEdit, PatchIds.NoAutoAfk, PatchIds.NoMailDelay, PatchIds.NoZoomFade, PatchIds.NameplateDistance];

    private static string[] MemoryIds() => PatchCatalog.All.Where(p => PatchCatalog.InMemory(p.Id)).Select(p => p.Id).ToArray();

    // Patches with on-disk counterparts the DLL's code patches can be compared with.
    private static string[] CodeIds() => MemoryIds().Except(ByteIdentical).Where(id => !PatchCatalog.Get(id).MemoryOnly).ToArray();

    private static string[] DiskIds() => MemoryIds().Where(id => !PatchCatalog.Get(id).MemoryOnly).ToArray();

    private sealed record DllSite(string Name, string Find, string Swap);

    // Every patch the DLL defines is a catalog patch, in the same order, and each of its sites matches the catalog's
    // pattern at the same position; byte swaps write the catalog's replacement. Nameplate Distance's one site is the
    // catalog's anchor. The setting defaults and limits are the launcher's.
    [Fact]
    public void DefinitionsMatchCatalog()
    {
        var lines = RunTool(TestExe.PatcherHost(), "--list").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var patches = new List<(string Id, List<DllSite> Sites)>();
        foreach (var line in lines.Where(l => l.StartsWith("patch ") || l.StartsWith("site ")))
        {
            if (line.StartsWith("patch "))
            {
                patches.Add((line[6..], []));
            }
            else
            {
                var parts = line[5..].Split('|');
                patches[^1].Sites.Add(new DllSite(parts[0], parts[1], parts[2]));
            }
        }
        Assert.Equal(MemoryIds(), patches.Select(p => p.Id));
        foreach (var (id, sites) in patches)
        {
            var catalog = PatchCatalog.Get(id).Replacements;
            if (PatchCatalog.Get(id).MemoryOnly)
            {
                Assert.Empty(catalog);
                Assert.NotEmpty(sites);
            }
            else if (id == PatchIds.NameplateDistance)
            {
                Assert.Equal([PatchCatalog.NameplateDistanceAnchor], sites.Select(s => s.Find));
            }
            else
            {
                Assert.Equal(catalog.Select(r => r.FindHex), sites.Select(s => s.Find));
                foreach (var (site, replacement) in sites.Zip(catalog).Where(p => p.First.Swap != "-"))
                {
                    Assert.True(site.Swap == replacement.ReplaceHex, $"{id} {site.Name}: swap {site.Swap}, catalog {replacement.ReplaceHex}");
                }
            }
        }
        var defaults = lines.Where(l => l.StartsWith("default ")).Select(l => l.Split(' ')).ToDictionary(p => p[1], p => p[2..]);
        int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal([PatchSettings.DefaultNameplateDistance, PatchSettings.MinNameplateDistance, PatchSettings.MaxNameplateDistance],
            defaults["NameplateDistance"].Select(Number));
        Assert.Equal([PatchSettings.DefaultStackSpacing, PatchSettings.MinStackSpacing, PatchSettings.MaxStackSpacing],
            defaults["StackSpacing"].Select(Number));
        Assert.Equal(PatcherConfig.FormatTiers(DamageColors.Default), defaults["DamageColors"][0]);
        Assert.Equal(DamageColors.MaxTiers, Number(defaults["DamageColors"][1]));
    }

    // The DLL builds the same HD portrait mask the on-disk patch writes.
    [Fact]
    public void PortraitMaskMatchesCatalog()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("launcher-mask-").FullName, "mask.bin");
        try
        {
            RunTool(TestExe.PatcherHost(), "--mask", path);
            Assert.True(PortraitMask.Bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    public static TheoryData<string> Scenarios()
    {
        var names = new TheoryData<string> { "All", "Distance 85", "None" };
        foreach (var id in ByteIdentical)
        {
            names.Add(id);
        }
        return names;
    }

    // The byte-swap patches on an exe with only Large Address Aware and the DLL Loader give the same mapped image as
    // writing them into the exe, and each one reports itself applied.
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void MatchesDiskPatches(string scenario)
    {
        var original = TestExe.OriginalBytes();
        var host = TestExe.PatcherHost();
        var ids = scenario switch
        {
            "All" => ByteIdentical,
            "Distance 85" => [PatchIds.NameplateDistance],
            "None" => [],
            _ => [scenario],
        };
        var config = new PatcherConfig(ids, scenario == "Distance 85" ? 85 : 60, 100, DamageColors.Default);
        var settings = new PatchSettings(config.NameplateDistance, config.StackSpacing, config.DamageColors, "DLLs");
        var baseExe = ExePatcher.Apply(original, Base, settings).Bytes;
        var expected = ExePatcher.Apply(original, Base.Concat(ids), settings);
        Assert.Empty(expected.Failed);

        var (image, run) = RunHost(host, baseExe, config);

        Assert.True(TestExe.MapImage(expected.Bytes).AsSpan().SequenceEqual(image), $"Image differs for {scenario}");
        Assert.Equal(ids.Order(), run.Patches.Keys.Order());
        Assert.All(run.Patches.Values, r => Assert.Equal(PatcherOutcome.Applied, r.Outcome));
    }

    // On an exe that already carries every patch on disk, the DLL changes nothing: the byte swaps read as already on,
    // and the code patches, whose original code is gone, are skipped.
    [Fact]
    public void DiskPatchedExeChangesNothing()
    {
        var original = TestExe.OriginalBytes();
        var host = TestExe.PatcherHost();
        var config = new PatcherConfig(DiskIds(), 60, 100, DamageColors.Default);
        var patched = ExePatcher.Apply(original, Base.Concat(DiskIds()), new PatchSettings(60, 100, DamageColors.Default, "DLLs")).Bytes;

        var (image, run) = RunHost(host, patched, config);

        Assert.True(TestExe.MapImage(patched).AsSpan().SequenceEqual(image));
        Assert.All(ByteIdentical, id => Assert.Equal(PatcherOutcome.AlreadyOn, run.Patches[id].Outcome));
        Assert.All(CodeIds(), id => Assert.Equal(PatcherOutcome.Skipped, run.Patches[id].Outcome));
    }

    // A patch whose bytes don't match is skipped with the reason, and nothing it would have written is left behind,
    // even when other parts of it matched; the other patches still apply.
    [Fact]
    public void MismatchedPatchIsSkippedWhole()
    {
        var original = TestExe.OriginalBytes();
        var host = TestExe.PatcherHost();
        var settings = new PatchSettings(DllFolder: "DLLs");
        var baseExe = ExePatcher.Apply(original, Base, settings).Bytes;
        var lastPart = PatchCatalog.Get(PatchIds.InterfaceEdit).Replacements[^1].Find;
        var at = Bytes.FindAll(baseExe, lastPart).Single();
        baseExe[at + lastPart.Length - 1] ^= 0xFF;
        var config = new PatcherConfig([PatchIds.InterfaceEdit, PatchIds.NoMailDelay], 60, 100, DamageColors.Default);

        var (image, run) = RunHost(host, baseExe, config);

        Assert.Equal(PatcherOutcome.Skipped, run.Patches[PatchIds.InterfaceEdit].Outcome);
        Assert.Contains("found 0 times", run.Patches[PatchIds.InterfaceEdit].Detail);
        Assert.Equal(PatcherOutcome.Applied, run.Patches[PatchIds.NoMailDelay].Outcome);
        var expected = ExePatcher.Apply(baseExe, [PatchIds.NoMailDelay], settings).Bytes;
        Assert.True(TestExe.MapImage(expected).AsSpan().SequenceEqual(image));
    }

    // The code patches all apply, with or without the DLL Loader's patch section, and change nothing outside their
    // sites; every site changed. (What each site now does is checked by PatcherTests.exe.)
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CodePatchesChangeOnlyTheirSites(bool withSection)
    {
        var original = TestExe.OriginalBytes();
        var host = TestExe.PatcherHost();
        var baseExe = withSection ? ExePatcher.Apply(original, Base, new PatchSettings(DllFolder: "DLLs")).Bytes : original;
        var config = new PatcherConfig(CodeIds(), 60, 100, DamageColors.Default);

        var (image, run) = RunHost(host, baseExe, config);

        Assert.Equal(CodeIds().Order(), run.Patches.Keys.Order());
        Assert.All(run.Patches.Values, r => Assert.Equal(PatcherOutcome.Applied, r.Outcome));
        var mapped = TestExe.MapImage(baseExe);
        var siteBytes = new HashSet<int>();
        foreach (var replacement in CodeIds().SelectMany(id => PatchCatalog.Get(id).Replacements))
        {
            var at = Bytes.FindAll(mapped, replacement.Find).Single();
            Assert.False(image.AsSpan(at, replacement.Find.Length).SequenceEqual(replacement.Find), $"Site {replacement.FindHex} unchanged");
            siteBytes.UnionWith(Enumerable.Range(at, replacement.Find.Length));
        }
        var outside = Enumerable.Range(0, mapped.Length).Where(i => !siteBytes.Contains(i) && mapped[i] != image[i]).Take(5).ToList();
        Assert.Empty(outside);
    }

    // Ground Target Macros, which exists only in memory, changes just the 9-byte prologue of Script_CastSpellByName
    // (a jump to the DLL), never its Lua registration entry, with or without the DLL Loader's patch section.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroundTargetsChangesOnlyItsEntry(bool withSection)
    {
        var original = TestExe.OriginalBytes();
        var host = TestExe.PatcherHost();
        var baseExe = withSection ? ExePatcher.Apply(original, Base, new PatchSettings(DllFolder: "DLLs")).Bytes : original;
        var config = new PatcherConfig([PatchIds.GroundTargets], 60, 100, DamageColors.Default);

        var (image, run) = RunHost(host, baseExe, config);

        Assert.Equal(PatcherOutcome.Applied, run.Patches[PatchIds.GroundTargets].Outcome);
        var mapped = TestExe.MapImage(baseExe);
        var entry = Bytes.FindAll(mapped, Bytes.FromHex("55 8B EC 81 EC D0 02 00 00 56 8B 75 08 6A 01 56 E8 3B DC 30 00")).Single();
        var changed = Enumerable.Range(0, mapped.Length).Where(i => mapped[i] != image[i]).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, i => Assert.InRange(i, entry, entry + 8));
        Assert.Equal(0xE9, image[entry]);
    }

    // The DLL's own unit tests (each patch's logic, the assembly shims, and every site on the real exe when one is
    // given) all pass.
    [Fact]
    public void NativeUnitTestsPass()
    {
        var tests = TestExe.PatcherTests();
        var start = new ProcessStartInfo(tests) { UseShellExecute = false, RedirectStandardOutput = true };
        if (TestExe.OriginalPath() is { } exe)
        {
            start.ArgumentList.Add(exe);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("0 failure(s)", output);
    }

    // The settings file round-trips the switches and settings, and leaves Large Address Aware and the loader out.
    [Fact]
    public void ConfigRoundTrips()
    {
        var config = new PatcherConfig([PatchIds.NoMailDelay, PatchIds.DamageColors], 75, 120, DamageColors.Parse("100K=00FF00,2.5M=FF0000"));
        var text = config.ToIni();
        var read = PatcherConfig.FromIni(text)!;
        Assert.Equal(config.On.Order(), read.On.Order());
        Assert.Equal(75, read.NameplateDistance);
        Assert.Equal(120, read.StackSpacing);
        Assert.Equal(config.DamageColors, read.DamageColors);
        Assert.DoesNotContain(PatchIds.DllLoader, text);
        Assert.DoesNotContain(PatchIds.LargeAddressAware, text);
    }

    // One bad tier in a hand-edited settings file makes the whole line fall back to the defaults, as the DLL does.
    [Fact]
    public void BadDamageTierFallsBackLikeTheDll()
    {
        var text = new PatcherConfig([PatchIds.DamageColors], 60, 100, DamageColors.Default).ToIni()
            .Replace(PatcherConfig.FormatTiers(DamageColors.Default), "100000=00FF00,oops");
        Assert.Equal(DamageColors.Default, PatcherConfig.FromIni(text)!.DamageColors);
    }

    // The DLL's status file reads back with each patch's outcome and reason.
    [Fact]
    public void StatusReadsBack()
    {
        var run = PatcherConfig.ReadRun("[Run]\nTime=2026-11-17T09:30:12\n\n[Patches]\nNoMailDelay=Applied\nNoZoomFade=AlreadyOn\nShortNumbers=Skipped: 8B 44 (found 0 times)\n")!;
        Assert.Equal(new DateTime(2026, 11, 17, 9, 30, 12), run.Time);
        Assert.Equal(PatcherOutcome.Applied, run.Patches[PatchIds.NoMailDelay].Outcome);
        Assert.Equal(PatcherOutcome.AlreadyOn, run.Patches[PatchIds.NoZoomFade].Outcome);
        Assert.Equal(new PatcherResult(PatcherOutcome.Skipped, "8B 44 (found 0 times)"), run.Patches[PatchIds.ShortNumbers]);
    }

    private static string RunTool(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private static (byte[] Image, PatcherRun Run) RunHost(string host, byte[] exe, PatcherConfig config)
    {
        var folder = Directory.CreateTempSubdirectory("launcher-host-").FullName;
        try
        {
            var exePath = Path.Combine(folder, "WowExt.exe");
            var iniPath = Path.Combine(folder, PatcherConfig.IniName);
            var imagePath = Path.Combine(folder, "image.bin");
            var statusPath = Path.Combine(folder, PatcherConfig.StatusName);
            File.WriteAllBytes(exePath, exe);
            File.WriteAllText(iniPath, config.ToIni());
            var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in new[] { exePath, iniPath, imagePath, statusPath })
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            return (File.ReadAllBytes(imagePath), PatcherConfig.ReadRun(File.ReadAllText(statusPath))!);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
