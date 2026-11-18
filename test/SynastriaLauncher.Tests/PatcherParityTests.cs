// Checks that the C# patcher writes the same exe as the retired SynastriaPatcher.ps1, step for step: each patch
// alone, everything, custom settings, and settings kept or changed on an already patched exe. The one intended
// difference, the DLL Loader also loading SynastriaLauncher.dll, is left out of the comparison. Skips unless
// SYNASTRIA_TEST_WOWEXT and SYNASTRIA_TEST_PATCHER are set and pwsh is on the PATH.

using System.Diagnostics;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class PatcherParityTests
{
    public sealed record Step(string Patch, int? Distance = null, int? Spacing = null, string? Colors = null, string? Folder = null);

    private const string CustomColors = "100K=00FF00,2.5M=FF0000";

    private static readonly Dictionary<string, Step[]> Scenarios = new()
    {
        ["All"] = [new("All")],
        ["Distance 80"] = [new(PatchIds.NameplateDistance, Distance: 80)],
        ["Stack 70"] = [new(PatchIds.NameplateStack, Spacing: 70)],
        ["Custom colors"] = [new(PatchIds.DamageColors, Colors: CustomColors)],
        ["Nested DLL folder"] = [new(PatchIds.DllLoader, Folder: @"Mods\Native")],
        ["Several at once"] = [new($"{PatchIds.HdPortraits},{PatchIds.NoAutoAfk},{PatchIds.NameplateSort}")],
        ["Distance kept"] = [new(PatchIds.NameplateDistance, Distance: 80), new(PatchIds.NameplateDistance)],
        ["Stack kept"] = [new(PatchIds.NameplateStack, Spacing: 70), new(PatchIds.NameplateStack)],
        ["Colors kept"] = [new(PatchIds.DamageColors, Colors: CustomColors), new(PatchIds.DamageColors)],
        ["Folder kept"] = [new(PatchIds.DllLoader, Folder: "Other"), new(PatchIds.DllLoader)],
        ["All twice"] = [new("All"), new("All")],
        ["Settings changed after all"] =
        [
            new("All"),
            new("All", Distance: 41, Spacing: 70, Colors: CustomColors, Folder: "Other"),
        ],
        ["Section added first, then all"] = [new(PatchIds.ShortNumbers), new("All")],
    };

    public static TheoryData<string> ScenarioNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in Scenarios.Keys.Concat(PatchCatalog.All.Where(p => !p.MemoryOnly).Select(p => p.Id)))
        {
            names.Add(name);
        }
        return names;
    }

    // The launcher and the script produce identical exes, backups and DLL folders after every step.
    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void MatchesScript(string scenario)
    {
        var original = TestExe.OriginalBytes();
        var script = TestExe.PatcherScript();
        var steps = Scenarios.TryGetValue(scenario, out var listed) ? listed : [new Step(scenario)];
        using var reference = new TestExe.GameCopy(original);
        using var launcher = new TestExe.GameCopy(original);
        var exe = new GameExe(launcher.Folder);
        foreach (var step in steps)
        {
            RunScript(script, reference.Folder, step);
            var ids = step.Patch == "All" ? ExePatcher.AvailableIds(exe.Read()) : step.Patch.Split(',');
            exe.Apply(ids, new PatchSettings(step.Distance, step.Spacing,
                step.Colors is null ? null : DamageColors.Parse(step.Colors), step.Folder));
            var launcherBytes = exe.Read();
            Assert.True(WithLauncherLoad(File.ReadAllBytes(reference.ExePath), launcherBytes).AsSpan().SequenceEqual(launcherBytes),
                $"Exe differs after step {step}");
            Assert.Equal(File.Exists(reference.ExePath + ".bak"), exe.HasBackup);
            Assert.Equal(Folders(reference.Folder), Folders(launcher.Folder));
        }
    }

    // The launcher's DLL Loader also loads SynastriaLauncher.dll, which the script's never did; those bytes are taken
    // from the launcher's exe so the rest is still compared.
    private static byte[] WithLauncherLoad(byte[] script, byte[] launcher)
    {
        var section = PatchSection.Find(launcher);
        var hasLoader = section is not null && PatchSection.Find(script) is not null
            && ExePatcher.Plan(launcher, PatchIds.DllLoader, PatchSettings.Keep).Status == PatchStatus.Applied;
        foreach (var (offset, length) in hasLoader ? PatchCatalog.PatcherLoadRanges : [])
        {
            var at = (int)section!.RawOffset + offset;
            launcher.AsSpan(at, length).CopyTo(script.AsSpan(at));
        }
        return script;
    }

    private static IEnumerable<string> Folders(string root) =>
        Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(root, d)).Order();

    private static void RunScript(string script, string gameFolder, Step step)
    {
        var appData = Directory.CreateTempSubdirectory("launcher-appdata-").FullName;
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-GamePath", gameFolder, "-Patch", step.Patch];
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        (string Name, object? Value)[] options =
            [("-NameplateDistance", step.Distance), ("-StackSpacing", step.Spacing), ("-DamageColors", step.Colors), ("-DllFolder", step.Folder)];
        foreach (var (name, value) in options.Where(o => o.Value is not null))
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value!.ToString()!);
        }
        start.Environment["LOCALAPPDATA"] = appData;
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Script failed on {step}: {output.Result}{errors.Result}");
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }
}
