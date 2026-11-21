using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Addons;
using SynastriaLauncher.Core.GitHub;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.App.ViewModels;

public enum LauncherPage
{
    Patches,
    Addons,
    Mods,
    Reforged,
    Wdm,
    Settings,
}

// Holds what every page shares: saved data, the game folder, the GitHub reader and the dialogs.
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatches), nameof(IsAddons), nameof(IsMods), nameof(IsReforged), nameof(IsWdm), nameof(IsSettings))]
    public partial LauncherPage Page { get; set; } = LauncherPage.Addons;

    public bool IsPatches => Page == LauncherPage.Patches;
    public bool IsAddons => Page == LauncherPage.Addons;
    public bool IsMods => Page == LauncherPage.Mods;
    public bool IsSettings => Page == LauncherPage.Settings;
    public bool IsReforged => Page == LauncherPage.Reforged;
    public bool IsWdm => Page == LauncherPage.Wdm;

    partial void OnPageChanged(LauncherPage value)
    {
        if (value == LauncherPage.Reforged)
        {
            Reforged.RefreshFixes();
            _ = Reforged.EnsureLoadedAsync();
        }
        else if (value == LauncherPage.Wdm)
        {
            _ = Wdm.EnsureLoadedAsync();
        }
    }

    [RelayCommand]
    private void Navigate(LauncherPage page) => Page = page;

    public LauncherData Data { get; }
    public GitHubKeyless GitHub { get; } = new();
    public IDialogs Dialogs { get; }

    public PlayViewModel Play { get; }
    public PatchesViewModel Patches { get; }
    public AddonsViewModel Addons { get; }
    public ModsViewModel Mods { get; }
    public ReforgedViewModel Reforged { get; }
    public WowExtViewModel WowExt { get; }
    public WdmViewModel Wdm { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameFolder), nameof(GameFolderLabel))]
    public partial string? GameFolder { get; set; }

    [ObservableProperty]
    public partial LauncherUpdate? LauncherUpdate { get; set; }

    [ObservableProperty]
    public partial string LauncherUpdateStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCheckingLauncher { get; set; }

    public bool HasGameFolder => GameFolder is not null;
    public string GameFolderLabel => GameFolder ?? "No game folder chosen";
    public string Version => $"Version {SelfUpdate.CurrentVersion.ToString(3)}";

    public bool KeepAddonBackups
    {
        get => Data.KeepAddonBackups;
        set
        {
            Data.KeepAddonBackups = value;
            Data.Save();
            OnPropertyChanged();
        }
    }

    public MainViewModel(IDialogs dialogs)
    {
        Dialogs = dialogs;
        Data = LauncherData.Load();
        Play = new PlayViewModel(this);
        Patches = new PatchesViewModel(this);
        Addons = new AddonsViewModel(this);
        Mods = new ModsViewModel(this);
        Reforged = new ReforgedViewModel(this);
        WowExt = new WowExtViewModel(this);
        Wdm = new WdmViewModel(this);
        GameFolder = Core.GameFolder.Detect(Data.GameFolder);
        if (Environment.ProcessPath is { } exe)
        {
            SelfUpdate.CleanUp(exe);
        }
    }

    public GameExe? Exe => GameFolder is null ? null : new GameExe(GameFolder);

    public async Task StartAsync()
    {
        await ReloadAllAsync();
        await CheckLauncherUpdateAsync();
        await WowExt.CheckAsync();
    }

    private async Task ReloadAllAsync()
    {
        await SyncGameFilesAsync();
        Patches.Load();
        await Mods.LoadAsync();
        await Mods.SyncReforgedFixesAsync();
        await Addons.CheckAllAsync();
        Play.Refresh();
    }

    // Keeps the launcher's own files in the game folder in step, at launcher start and before the game starts:
    // SynastriaLauncher.dll with this launcher's build, and the spell effects fix with Reforged's patch (changes made
    // outside the launcher included). While the game runs the files are in use, so this waits.
    public async Task SyncGameFilesAsync()
    {
        if (GameFolder is { } game && !GameProcess.IsRunning())
        {
            try
            {
                await Task.Run(() =>
                {
                    new GameExe(game).RefreshPatcherDll(PatcherConfig.EmbeddedDll());
                    Core.Reforged.SpellEffectsFix.Sync(game);
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                App.LogCrash(e);
            }
        }
    }

    [RelayCommand]
    private async Task ChooseGameFolderAsync()
    {
        var folder = await Dialogs.PickFolderAsync("Choose Your Synastria WoW Folder", GameFolder);
        if (folder is not null && Core.GameFolder.IsGameFolder(folder))
        {
            GameFolder = folder;
            Data.GameFolder = folder;
            Data.WowExtVersion = null;
            Data.Save();
            Reforged.GameFolderChanged();
            Wdm.GameFolderChanged();
            await ReloadAllAsync();
            await WowExt.CheckAsync();
        }
        else if (folder is not null)
        {
            await Dialogs.AlertAsync("Not a Game Folder", $"WowExt.exe isn't in {folder}. Choose the folder that has WowExt.exe in it.");
        }
    }

    [RelayCommand]
    private async Task CheckLauncherUpdateAsync()
    {
        LauncherUpdateStatus = "Checking for Launcher Updates";
        IsCheckingLauncher = true;
        try
        {
            LauncherUpdate = await SelfUpdate.CheckAsync(GitHub, SelfUpdate.CurrentVersion);
            LauncherUpdateStatus = LauncherUpdate is null ? "Up to Date" : $"Version {LauncherUpdate.Tag} Available";
        }
        catch (HttpRequestException)
        {
            LauncherUpdateStatus = "Couldn't Check for Launcher Updates";
        }
        finally
        {
            IsCheckingLauncher = false;
        }
    }

    [RelayCommand]
    private async Task UpdateLauncherAsync()
    {
        var exe = Environment.ProcessPath;
        if (LauncherUpdate is not null && exe is not null)
        {
            try
            {
                LauncherUpdateStatus = "Downloading the Update";
                var incoming = await SelfUpdate.DownloadAsync(GitHub, LauncherUpdate, exe);
                GameProcess.Start(incoming, SelfUpdate.FinishArgs(exe, Environment.ProcessId));
                Environment.Exit(0);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                LauncherUpdateStatus = "Update Failed";
                App.LogCrash(e);
                await Dialogs.AlertAsync("Update Failed",
                    $"{(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message)}\n\nThe details were saved to {App.CrashLogPath}.");
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackups), nameof(BackupSummary))]
    public partial IReadOnlyList<BackupItem> BackupItems { get; set; } = [];

    public bool HasBackups => BackupItems.Count > 0;

    public string BackupSummary => BackupItems.Count switch
    {
        0 => "Nothing to clean up. The original WowExt.exe backup is always kept for Restore Original Game.",
        var n => $"{n} backup{(n == 1 ? "" : "s")}, {Backups.FormatSize(BackupItems.Sum(b => b.Bytes))}: old addon versions, replaced settings files, old WowExt files and switched-off Project Reforged parts. "
            + "The original WowExt.exe backup is always kept.",
    };

    public void RefreshBackups()
    {
        var dlls = Exe is { } exe ? ExePatcher.ReadSettings(exe.Read()).DllFolder ?? DllFolder.Default : DllFolder.Default;
        BackupItems = GameFolder is null ? [] : Backups.Find(GameFolder, Mods.Items.Select(i => i.Mod), dlls);
    }

    [RelayCommand]
    private async Task CleanUpBackupsAsync()
    {
        RefreshBackups();
        var items = BackupItems;
        var list = string.Join("\n", items.Take(12).Select(b => $"•  {b.Label} ({Backups.FormatSize(b.Bytes)})"))
            + (items.Count > 12 ? $"\n•  and {items.Count - 12} more" : "");
        var confirmed = items.Count > 0 && await Dialogs.ConfirmAsync("Clean Up Backups",
            $"Move these to the Recycle Bin?\n\n{list}", "Move to Recycle Bin");
        if (confirmed)
        {
            try
            {
                Backups.Recycle(items);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                await Dialogs.AlertAsync("Couldn't Clean Up Everything", e.Message);
            }
            RefreshBackups();
        }
    }

    [RelayCommand]
    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(LauncherData.DefaultFolder);
        GameProcess.OpenUrl(LauncherData.DefaultFolder);
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (GameFolder is not null)
        {
            GameProcess.OpenUrl(GameFolder);
        }
    }

    // Windows reports ERROR_VIRUS_INFECTED (225) or ERROR_VIRUS_DELETED (226) when Defender blocks a file.
    private static bool IsAntivirusBlock(IOException e) => (e.HResult & 0xFFFF) is 225 or 226;

    // Writes to the game folder can fail without admin rights (under Program Files) or while the game runs.
    public async Task<bool> RunGameWriteAsync(string what, Func<Task> write)
    {
        var done = false;
        if (GameProcess.IsRunning())
        {
            await Dialogs.AlertAsync("Close the Game First", $"The game is running, so the launcher can't {what}. Close it and try again.");
        }
        else
        {
            try
            {
                _gameWrites++;
                await write();
                done = true;
            }
            catch (OperationCanceledException)
            {
                done = false;
            }
            catch (UnauthorizedAccessException)
            {
                var restart = await Dialogs.ConfirmAsync("Administrator Rights Needed",
                    "Windows denied access to the game folder (likely because it's under Program Files). Restart the launcher as administrator?",
                    "Restart as Administrator");
                if (restart && GameProcess.RestartAsAdmin())
                {
                    Environment.Exit(0);
                }
            }
            catch (IOException e) when (IsAntivirusBlock(e))
            {
                await Dialogs.AlertAsync("Blocked by Your Antivirus",
                    $"Windows Security (or your antivirus) flagged a download needed to {what} and blocked it, so nothing was installed. "
                    + "The launcher doesn't work around antivirus blocks. Mods that hook into the game are sometimes flagged even when "
                    + "they're harmless, but only you can decide to trust one: its home page has its author's notes.");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException)
            {
                await Dialogs.AlertAsync("Something Went Wrong", e.Message);
            }
            finally
            {
                _gameWrites--;
            }
        }
        return done;
    }

    private int _gameWrites;

    // Installing, updating or removing something in the game folder; the game mustn't start meanwhile.
    public bool IsWritingGame => _gameWrites > 0;
}
