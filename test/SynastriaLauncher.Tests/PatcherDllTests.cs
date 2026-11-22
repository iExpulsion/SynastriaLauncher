// Covers a SynastriaLauncher.dll that went missing from the game folder (an antivirus removing it): GameExe reports it
// while patches are set up and the launcher puts it back. Needs no real WowExt.exe.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class PatcherDllTests
{
    private static readonly byte[] FakeDll = [0x4D, 0x5A, 1, 2, 3];

    private static PatcherConfig Config(params string[] on) => new(on, 80, 100, DamageColors.Default);

    // With patches in the settings file and no DLL, the DLL counts as missing.
    [Fact]
    public void MissingDllWithPatchesIsReported()
    {
        using var game = new TestExe.GameCopy([0]);
        var exe = new GameExe(game.Folder);
        File.WriteAllText(exe.IniPath, Config(PatchIds.NoMailDelay).ToIni());
        Assert.True(exe.PatcherDllMissing);
    }

    // No settings file, or one with no patches on, means nothing needs the DLL.
    [Fact]
    public void NoPatchesMeansNothingIsMissing()
    {
        using var game = new TestExe.GameCopy([0]);
        var exe = new GameExe(game.Folder);
        Assert.False(exe.PatcherDllMissing);
        File.WriteAllText(exe.IniPath, Config().ToIni());
        Assert.False(exe.PatcherDllMissing);
    }

    // Refreshing puts a missing DLL back while its settings file is there.
    [Fact]
    public void RefreshRestoresMissingDll()
    {
        using var game = new TestExe.GameCopy([0]);
        var exe = new GameExe(game.Folder);
        File.WriteAllText(exe.IniPath, Config(PatchIds.NoMailDelay).ToIni());
        Assert.True(exe.RefreshPatcherDll(FakeDll));
        Assert.True(exe.HasPatcherDll(FakeDll));
        Assert.False(exe.PatcherDllMissing);
    }

    // Without a settings file the launcher never adds the DLL to a game that doesn't use it.
    [Fact]
    public void RefreshLeavesUnpatchedGameAlone()
    {
        using var game = new TestExe.GameCopy([0]);
        var exe = new GameExe(game.Folder);
        Assert.False(exe.RefreshPatcherDll(FakeDll));
        Assert.False(File.Exists(exe.DllPath));
    }
}
