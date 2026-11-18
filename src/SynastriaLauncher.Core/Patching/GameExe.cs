namespace SynastriaLauncher.Core.Patching;

public sealed record StateResult(ApplyResult Result, bool Written, bool FromOriginal);

// The game folder's WowExt.exe on disk: applies patches with a one-time backup of the original, and restores it.
public sealed class GameExe(string gameFolder)
{
    public string GameFolder { get; } = gameFolder;
    public string ExePath => Path.Combine(GameFolder, Core.GameFolder.ExeName);
    public string BackupPath => ExePath + ".bak";
    public bool HasBackup => File.Exists(BackupPath);

    public byte[] Read() => File.ReadAllBytes(ExePath);

    // Writes the exe only when every patch fits and something changes. The DLL Loader's folder is created
    // when that patch is asked for, so there's a place to put DLLs.
    public ApplyResult Apply(IEnumerable<string> ids, PatchSettings settings)
    {
        var wanted = ids.ToList();
        var result = ExePatcher.Apply(Read(), wanted, settings);
        if (result.HasChanges)
        {
            if (!HasBackup)
            {
                File.Copy(ExePath, BackupPath);
                File.SetAttributes(BackupPath, FileAttributes.Normal);
            }
            WriteKeepingAttributes(ExePath, result.Bytes);
        }
        if (wanted.Contains(PatchIds.DllLoader) && DllFolderPath() is { } folder)
        {
            Directory.CreateDirectory(folder);
        }
        return result;
    }

    // Patches that are on now (fully or partly), for the Patches page's switches.
    public static IReadOnlyList<string> AppliedIds(byte[] bytes) =>
        PatchCatalog.All
            .Where(p => !p.Hidden && ExePatcher.Plan(bytes, p, PatchSettings.Keep).Status is PatchStatus.Applied or PatchStatus.PartlyApplied)
            .Select(p => p.Id)
            .ToList();

    // Makes the exe match the switches. A patch can't be taken out on its own, so when one is switched off (or an
    // older pack's patch section blocks the update) the wanted set is applied to the backed-up original instead.
    public StateResult ApplyState(IReadOnlyCollection<string> on, PatchSettings settings)
    {
        var current = Read();
        var wanted = on.Append(PatchIds.LargeAddressAware).Distinct().ToList();
        var turnedOff = AppliedIds(current).Except(wanted).Any();
        var blocked = ExePatcher.Apply(current, wanted, settings).Failed.Count > 0;
        var fromOriginal = HasBackup && (turnedOff || blocked);
        var result = ExePatcher.Apply(fromOriginal ? File.ReadAllBytes(BackupPath) : current, wanted, settings);
        var changes = result.Failed.Count == 0 && !result.Bytes.AsSpan().SequenceEqual(current);
        if (changes)
        {
            if (!HasBackup)
            {
                File.Copy(ExePath, BackupPath);
                File.SetAttributes(BackupPath, FileAttributes.Normal);
            }
            WriteKeepingAttributes(ExePath, result.Bytes);
        }
        if (changes && wanted.Contains(PatchIds.DllLoader) && DllFolderPath() is { } folder)
        {
            Directory.CreateDirectory(folder);
        }
        if (result.Failed.Count == 0)
        {
            RemovePatcherFiles();
        }
        return new StateResult(result, changes, fromOriginal);
    }

    public string DllPath => Path.Combine(GameFolder, PatcherConfig.DllName);
    public string IniPath => Path.Combine(GameFolder, PatcherConfig.IniName);
    public string StatusPath => Path.Combine(GameFolder, PatcherConfig.StatusName);

    // An exe from before the in-memory patches: one carrying patches other than Large Address Aware and the DLL
    // Loader, or the older DLL Loader that doesn't load SynastriaLauncher.dll.
    public static bool HasDiskPatches(byte[] bytes) =>
        AppliedIds(bytes).Any(PatchCatalog.InMemory) || HasOlderLoader(bytes);

    public static bool HasOlderLoader(byte[] bytes) =>
        PeLayout.Read(bytes) is { } layout
        && BitConverter.ToUInt32(bytes, layout.EntryPointField) == PatchCatalog.LoaderEntry
        && ExePatcher.Plan(bytes, PatchIds.DllLoader, PatchSettings.Keep).Status == PatchStatus.Unavailable;

    // What SynastriaLauncher.dll will patch: this exe, or the backed-up original while patches are still on disk.
    public byte[] BaseBytes()
    {
        var current = Read();
        return HasDiskPatches(current) && HasBackup ? File.ReadAllBytes(BackupPath) : current;
    }

    public PatcherConfig? ReadConfig() => File.Exists(IniPath) ? PatcherConfig.FromIni(File.ReadAllText(IniPath)) : null;

    public PatcherRun? ReadRun() => File.Exists(StatusPath) ? PatcherConfig.ReadRun(File.ReadAllText(StatusPath)) : null;

    public bool HasPatcherDll(byte[]? dll) =>
        dll is not null && File.Exists(DllPath) && File.ReadAllBytes(DllPath).AsSpan().SequenceEqual(dll);

    // Puts the patches in memory: the exe keeps only Large Address Aware and the DLL Loader (rebuilt from the
    // original once if it still carries patches on disk), SynastriaLauncher.dll goes next to it, and the switches and
    // settings go into SynastriaLauncher.ini. With the loader off and no patches on, the DLL and its files are removed.
    public StateResult ApplyMemory(PatcherConfig config, bool loader, string? dllFolder, byte[]? dll)
    {
        var useLoader = loader || config.On.Count > 0;
        var patcher = config.On.Count == 0 || dll is not null ? dll
            : throw new InvalidDataException("This launcher build doesn't include SynastriaLauncher.dll, so the patches can't be applied.");
        string[] wanted = useLoader ? [PatchIds.LargeAddressAware, PatchIds.DllLoader] : [PatchIds.LargeAddressAware];
        var settings = new PatchSettings(DllFolder: dllFolder);
        var current = Read();
        if (IsReplacedByHand(current))
        {
            ArchiveBackup();
        }
        var leftOver = HasDiskPatches(current) || AppliedIds(current).Except(wanted).Any();
        var blocked = ExePatcher.Apply(current, wanted, settings).Failed.Count > 0;
        var fromOriginal = HasBackup && (leftOver || blocked);
        var result = ExePatcher.Apply(fromOriginal ? File.ReadAllBytes(BackupPath) : current, wanted, settings);
        var changes = result.Failed.Count == 0 && !result.Bytes.AsSpan().SequenceEqual(current);
        if (changes)
        {
            if (!HasBackup)
            {
                File.Copy(ExePath, BackupPath);
                File.SetAttributes(BackupPath, FileAttributes.Normal);
            }
            WriteKeepingAttributes(ExePath, result.Bytes);
        }
        if (result.Failed.Count == 0 && useLoader)
        {
            if (DllFolderPath() is { } folder)
            {
                Directory.CreateDirectory(folder);
            }
            if (patcher is not null && !HasPatcherDll(patcher))
            {
                File.WriteAllBytes(DllPath, patcher);
            }
            File.WriteAllText(IniPath, config.ToIni());
            File.Delete(StatusPath);
        }
        else if (result.Failed.Count == 0)
        {
            RemovePatcherFiles();
        }
        return new StateResult(result, changes, fromOriginal);
    }

    // A new original exe (a WowExt update) becomes WowExt.exe.bak and WowExt.exe, then the patches that were on go
    // back on in memory from SynastriaLauncher.ini, with the DLL folder the old exe held.
    public StateResult ReplaceOriginal(byte[] original, byte[]? dll)
    {
        var before = Read();
        var wasOn = AppliedIds(before);
        var old = ExePatcher.ReadSettings(before);
        WriteKeepingAttributes(ExePath, original);
        WriteAtomic(BackupPath, original);
        var config = ReadConfig() ?? new PatcherConfig([], PatchSettings.DefaultNameplateDistance, PatchSettings.DefaultStackSpacing, DamageColors.Default);
        return ApplyMemory(config, wasOn.Contains(PatchIds.DllLoader), old.DllFolder, dll);
    }

    public void RemovePatcherFiles()
    {
        foreach (var name in PatcherConfig.FileNames)
        {
            File.Delete(Path.Combine(GameFolder, name));
        }
    }

    // Puts the original back, unless the exe was replaced by hand (a newer WowExt): then that one stays and the old
    // original goes to the backups folder instead of downgrading the game.
    public void Restore()
    {
        if (IsReplacedByHand(Read()))
        {
            ArchiveBackup();
        }
        else
        {
            WriteKeepingAttributes(ExePath, File.ReadAllBytes(BackupPath));
        }
        RemovePatcherFiles();
    }

    // A newer WowExt.exe put in by hand: there's a backup, the exe carries no patches written the old way, and it
    // isn't the backup with the launcher's patches on it.
    public bool IsReplacedByHand(byte[] current) => HasBackup && !HasDiskPatches(current) && !IsPatchedCopyOfBackup(current);

    // The exe is the backed-up original with the patches it carries applied, not a different exe put in by hand.
    public bool IsPatchedCopyOfBackup(byte[] current)
    {
        var ids = PatchCatalog.All
            .Where(p => ExePatcher.Plan(current, p, PatchSettings.Keep).Status == PatchStatus.Applied)
            .Select(p => p.Id)
            .ToList();
        var held = ExePatcher.ReadSettings(current);
        var rebuilt = ExePatcher.Apply(File.ReadAllBytes(BackupPath), ids,
            new PatchSettings(held.NameplateDistance, held.StackSpacing, held.DamageColors, held.DllFolder));
        return rebuilt.Failed.Count == 0 && rebuilt.Bytes.AsSpan().SequenceEqual(current);
    }

    // Moves an outdated WowExt.exe.bak into _WowExtBackups, so the current exe can become the original.
    private void ArchiveBackup()
    {
        var folder = Client.WowExtUpdate.BackupFolder(GameFolder, null);
        Directory.CreateDirectory(folder);
        File.Move(BackupPath, Path.Combine(folder, Core.GameFolder.ExeName), overwrite: true);
    }

    public string? DllFolderPath() =>
        ExePatcher.ReadSettings(Read()).DllFolder is { } folder ? Path.Combine(GameFolder, folder) : null;

    private static void WriteKeepingAttributes(string path, byte[] bytes)
    {
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, FileAttributes.Normal);
        try
        {
            WriteAtomic(path, bytes);
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }
    }

    // Writes beside the file and swaps it in, so a crash or a full disk never leaves a half-written exe.
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
