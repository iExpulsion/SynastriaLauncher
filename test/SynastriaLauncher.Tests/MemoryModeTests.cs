// Covers GameExe's in-memory mode: the exe keeps only Large Address Aware and the DLL Loader, the DLL and settings
// file go next to it, an exe patched on disk is rebuilt from the original once, and switching everything off or
// going back to on-disk patches removes the DLL's files. Skips unless SYNASTRIA_TEST_WOWEXT is set.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

public sealed class MemoryModeTests
{
    private static readonly byte[] FakeDll = [0x4D, 0x5A, 1, 2, 3];
    private static readonly PatcherConfig Config =
        new([PatchIds.NoMailDelay, PatchIds.NameplateDistance], 80, 100, DamageColors.Default);

    private static byte[] BaseExe(byte[] original, string folder = "DLLs") =>
        ExePatcher.Apply(original, [PatchIds.LargeAddressAware, PatchIds.DllLoader], new PatchSettings(DllFolder: folder)).Bytes;

    // The loader's added step calls into its new routine, which calls the existing load helper with the DLL's name.
    [Fact]
    public void LoaderCallsPatcherLoad()
    {
        var section = new byte[0x2000];
        foreach (var routine in PatchCatalog.Get(PatchIds.DllLoader).Routines)
        {
            routine.Code.CopyTo(section, routine.Offset);
        }
        static int CallTarget(byte[] code, int at) => at + 5 + BitConverter.ToInt32(code, at + 1);
        Assert.Equal(0xE8, section[0x1930]);
        Assert.Equal(0x19C0, CallTarget(section, 0x1930));
        Assert.Equal(0xE8, section[0x19D2]);
        var loadHelper = CallTarget(section, 0x19D2);
        Assert.Equal(CallTarget(section, 0x1997), loadHelper);
        Assert.Equal(PatcherConfig.DllName + "\0", Bytes.Latin1.GetString(section, 0x19E0, PatcherConfig.DllName.Length + 1));
        var nameAddress = BitConverter.ToUInt32(section, 0x19C3);
        Assert.Equal(0x400000 + PatchSection.Rva + 0x19E0, nameAddress);
    }

    // On an unpatched game the exe gets only the two exe patches, and the DLL, settings file and DLL folder appear.
    [Fact]
    public void ApplyWritesBaseExeAndFiles()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        var result = exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        Assert.True(result.Written);
        Assert.Equal(BaseExe(original), exe.Read());
        Assert.Equal(original, File.ReadAllBytes(exe.BackupPath));
        Assert.True(exe.HasPatcherDll(FakeDll));
        Assert.Equal(Config.On, exe.ReadConfig()!.On);
        Assert.Equal(80, exe.ReadConfig()!.NameplateDistance);
        Assert.True(Directory.Exists(Path.Combine(game.Folder, "DLLs")));
        Assert.False(GameExe.HasDiskPatches(exe.Read()));
    }

    // A newer WowExt.exe put in by hand becomes the original: Apply patches it rather than the old backup, and the
    // old backup goes to _WowExtBackups.
    [Fact]
    public void HandReplacedExeIsNotDowngraded()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        Assert.True(exe.IsPatchedCopyOfBackup(exe.Read()));
        var newer = NewerExe(original);
        File.WriteAllBytes(exe.ExePath, newer);
        Assert.True(exe.IsReplacedByHand(newer));
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        Assert.Equal(newer, File.ReadAllBytes(exe.BackupPath));
        Assert.Equal(BaseExe(newer), exe.Read());
        Assert.Single(Directory.GetFiles(Path.Combine(game.Folder, "_WowExtBackups"), "WowExt.exe", SearchOption.AllDirectories));
    }

    // Restore Original leaves a hand-replaced exe in place instead of writing the older backup over it.
    [Fact]
    public void RestoreKeepsHandReplacedExe()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        var newer = NewerExe(original);
        File.WriteAllBytes(exe.ExePath, newer);
        exe.Restore();
        Assert.Equal(newer, exe.Read());
        Assert.False(exe.HasBackup);
        Assert.False(File.Exists(exe.DllPath));
    }

    // Restore Original on a normally patched exe puts the original back.
    [Fact]
    public void RestorePutsOriginalBack()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        exe.Restore();
        Assert.Equal(original, exe.Read());
    }

    // A different build of the same exe: the PE timestamp changed.
    private static byte[] NewerExe(byte[] original)
    {
        var newer = (byte[])original.Clone();
        newer[BitConverter.ToInt32(newer, 0x3C) + 8] ^= 0xFF;
        return newer;
    }

    // An exe with patches written on disk is rebuilt from the original, leaving only the exe patches.
    [Fact]
    public void DiskPatchesAreMigrated()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyState(ExePatcher.AvailableIds(original), new PatchSettings(60, 100, DamageColors.Default, "Mods"));
        Assert.True(GameExe.HasDiskPatches(exe.Read()));
        Assert.Equal(original, exe.BaseBytes());
        var result = exe.ApplyMemory(Config, loader: true, "Mods", FakeDll);
        Assert.True(result.FromOriginal);
        Assert.Equal(BaseExe(original, "Mods"), exe.Read());
        Assert.False(GameExe.HasDiskPatches(exe.Read()));
    }

    // A DLL Loader from before the launcher loaded SynastriaLauncher.dll counts as a disk patch and is replaced.
    [Fact]
    public void OlderLoaderIsReplaced()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var older = BaseExe(original);
        var section = (int)PatchSection.Find(older)!.RawOffset;
        Bytes.FromHex("47 BE 00 FA DF 00").CopyTo(older, section + 0x1930);
        Array.Clear(older, section + 0x19C0, 0x35);
        File.Copy(game.ExePath, game.ExePath + ".bak");
        File.WriteAllBytes(game.ExePath, older);
        var exe = new GameExe(game.Folder);
        Assert.True(GameExe.HasOlderLoader(older));
        Assert.True(exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll).FromOriginal);
        Assert.Equal(BaseExe(original), exe.Read());
    }

    // Switching every patch and the loader off leaves Large Address Aware alone and removes the DLL's files.
    [Fact]
    public void AllOffRemovesFiles()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        File.WriteAllText(exe.StatusPath, "[Patches]\n");
        exe.ApplyMemory(Config with { On = [] }, loader: false, "DLLs", FakeDll);
        Assert.Equal(ExePatcher.Apply(original, [PatchIds.LargeAddressAware], PatchSettings.Keep).Bytes, exe.Read());
        Assert.All(PatcherConfig.FileNames, name => Assert.False(File.Exists(Path.Combine(game.Folder, name))));
    }

    // Switching only the patches off keeps the loader for DLL mods and writes them off in the settings file.
    [Fact]
    public void PatchesOffKeepsLoader()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        var result = exe.ApplyMemory(Config with { On = [] }, loader: true, "DLLs", FakeDll);
        Assert.False(result.Written);
        Assert.Empty(exe.ReadConfig()!.On);
        Assert.Equal(BaseExe(original), exe.Read());
    }

    // A launcher build without the DLL refuses to switch patches on and changes nothing.
    [Fact]
    public void MissingDllChangesNothing()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        Assert.Throws<InvalidDataException>(() => exe.ApplyMemory(Config, loader: true, "DLLs", null));
        Assert.Equal(original, exe.Read());
        Assert.False(File.Exists(exe.IniPath));
    }

    // Going back to on-disk patches removes the DLL's files, so nothing is patched twice; Restore does too.
    [Fact]
    public void DiskModeAndRestoreRemoveFiles()
    {
        var original = TestExe.OriginalBytes();
        using var game = new TestExe.GameCopy(original);
        var exe = new GameExe(game.Folder);
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        exe.ApplyState([PatchIds.NoMailDelay], PatchSettings.Keep);
        Assert.False(File.Exists(exe.DllPath));
        exe.ApplyMemory(Config, loader: true, "DLLs", FakeDll);
        exe.Restore();
        Assert.Equal(original, exe.Read());
        Assert.False(File.Exists(exe.IniPath));
    }
}
