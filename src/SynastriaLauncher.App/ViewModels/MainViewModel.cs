using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Accounts;
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
    public AccountsViewModel Accounts { get; }

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
        Accounts = new AccountsViewModel(this);
        Accounts.Load();
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
        await CheckThirdPartyAsync();
    }

    // Started from a shortcut: what the Launch button needs (the launcher's game files, patches and mods in step),
    // then the game with the asked-for account, or the Launch button's own. True when the game started.
    public async Task<bool> QuickLaunchAsync(LaunchRequest request)
    {
        await SyncGameFilesAsync();
        Patches.Load();
        await Mods.LoadAsync();
        await Mods.SyncReforgedFixesAsync();
        Play.Refresh();
        var target = Data.LaunchTarget;
        var account = Data.FindAccount(request.Account ?? target?.Account);
        var character = request.Account is not null ? request.Character : request.Character ?? target?.Character;
        var started = false;
        if (request.Account is not null && account is null)
        {
            await Dialogs.AlertAsync("Account Not Found", $"There's no saved account named {request.Account}. Add it in Settings, under Accounts.");
        }
        else
        {
            started = await Play.StartGameAsync(account, character);
        }
        return started;
    }

    private async Task CheckThirdPartyAsync()
    {
        await Wdm.CheckIfInstalledAsync();
        await Reforged.CheckIfInstalledAsync();
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
        if (GameFolder is { } game && !GameProcess.IsRunning(game))
        {
            try
            {
                await Task.Run(() => new GameExe(game).RefreshPatcherDll(PatcherConfig.EmbeddedDll()));
                PatcherDllBlocked = false;
            }
            catch (IOException e) when (IsAntivirusBlock(e))
            {
                PatcherDllBlocked = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                App.LogCrash(e);
            }
            try
            {
                await Task.Run(() => SyncSharedChat(game));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                App.LogCrash(e);
            }
            try
            {
                await Task.Run(() => Core.Reforged.SpellEffectsFix.Sync(game));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                App.LogCrash(e);
            }
        }
    }

    // The antivirus refused SynastriaLauncher.dll the last time the launcher put it back.
    public bool PatcherDllBlocked { get; private set; }

    // Patches are on but SynastriaLauncher.dll, which loads them, isn't in the game folder; empty when all is well.
    public string PatcherDllWarning => Exe is not { PatcherDllMissing: true } ? ""
        : PatcherDllBlocked ? "Your antivirus blocks SynastriaLauncher.dll, so no patches load"
        : "SynastriaLauncher.dll is missing, so no patches load; press Apply Changes on Patches";

    public const string PatcherDllAntivirusNote =
        "The file it blocked is SynastriaLauncher.dll, the launcher's own patcher, which applies your patches as the game "
        + "starts. Patchers like it are sometimes flagged because they change the game while it runs. If you trust it, "
        + "restore it from Windows Security's Protection History (or your antivirus's quarantine) and allow it, then press "
        + "Apply Changes on Patches.";

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
            await CheckThirdPartyAsync();
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
        var n => $"{n} backup{(n == 1 ? "" : "s")}, {Backups.FormatSize(BackupItems.Sum(b => b.Bytes))}: old addon versions, replaced settings files, old WowExt files, switched-off Project Reforged parts, removed addons' settings moved aside and settings a character copy replaced. "
            + "The original WowExt.exe backup is always kept.",
    };

    public void RefreshBackups()
    {
        var dlls = Exe is { } exe ? ExePatcher.ReadSettings(exe.Read()).DllFolder ?? DllFolder.Default : DllFolder.Default;
        BackupItems = GameFolder is null ? [] : Backups.Find(GameFolder, Mods.Items.Select(i => i.Mod), dlls);
        LeftoverSettings = GameFolder is null ? [] : SavedSettings.Leftover(GameFolder);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeftoverSettings), nameof(LeftoverSettingsSummary))]
    public partial IReadOnlyList<SavedSettingsFile> LeftoverSettings { get; set; } = [];

    public bool HasLeftoverSettings => LeftoverSettings.Count > 0;

    public string LeftoverSettingsSummary => SavedSettings.Addons(LeftoverSettings) switch
    {
        [] => "No saved settings left behind by removed addons.",
        var addons => $"{addons.Count} removed addon{(addons.Count == 1 ? "" : "s")} left settings in WTF: "
            + string.Join(", ", addons.Take(5)) + (addons.Count > 5 ? $" and {addons.Count - 5} more" : "")
            + ". Moving them aside keeps them in WTF\\_RemovedAddonSettings until you clean up backups.",
    };

    // Starts from the character Launch logs in as, when the game has kept settings for it.
    [RelayCommand]
    private async Task CopyCharacterSettingsAsync()
    {
        var characters = GameFolder is null ? [] : CharacterSettings.Characters(GameFolder);
        var launch = Accounts.LaunchChoice?.Target;
        var from = characters.FirstOrDefault(c => launch?.Character is { } name
            && SavedAccount.SameCharacter(c.Name, name) && string.Equals(c.Account, launch.Account, StringComparison.OrdinalIgnoreCase));
        var form = characters.Count < 2 ? null : await Dialogs.CopyCharacterSettingsAsync(characters, from);
        if (characters.Count < 2)
        {
            await Dialogs.AlertAsync("Not Enough Characters",
                "The game keeps settings for a character once it has logged in on it. Log in on both characters once, then try again.");
        }
        else if (form is not null)
        {
            CharacterCopyResult? result = null;
            await RunGameWriteAsync("copy character settings", () =>
            {
                result = CharacterSettings.Copy(GameFolder!, form.From, form.To, form.Parts, DateTime.Now);
                return Task.CompletedTask;
            });
            if (result is not null)
            {
                RefreshBackups();
                await Dialogs.AlertAsync(result.BackupFolder is null ? "Nothing to Copy" : "Settings Copied",
                    result.Changes.Count == 0
                        ? $"{(form.To.Count == 1 ? form.To[0].Name + " already has" : "They already have")} {form.From.Name}'s settings."
                        : string.Join("\n", result.Changes.Take(15).Select(c => $"•  {c}"))
                            + (result.Changes.Count > 15 ? $"\n•  and {result.Changes.Count - 15} more" : "")
                            + (result.BackupFolder is null ? "" : "\n\nThe replaced files are in Settings, Backups until you clean up."));
            }
        }
    }

    private IReadOnlyList<WtfCharacter> SharedChatGroup(string game) =>
        CharacterSettings.Characters(game).Where(c => Data.SharedChatCharacters.Any(c.HasKey)).ToList();

    private void SyncSharedChat(string game)
    {
        var group = Data.SharedChatCharacters.Count > 1 ? SharedChatGroup(game) : [];
        if (group.Count > 1)
        {
            CharacterSettings.SyncChat(game, group);
        }
    }

    public string SharedChatSummary => Data.SharedChatCharacters.Count switch
    {
        0 => "Keep chat windows the same on several characters: change them on any one, and the others get them the next time you launch the game.",
        var n => $"{n} characters share chat windows. Changes on any of them reach the others the next time you launch the game.",
    };

    [RelayCommand]
    private async Task ShareChatWindowsAsync()
    {
        var characters = GameFolder is null ? [] : CharacterSettings.Characters(GameFolder);
        var picked = characters.Count < 2 ? null : await Dialogs.PickSharedChatAsync(characters, SharedChatGroup(GameFolder!));
        if (characters.Count < 2)
        {
            await Dialogs.AlertAsync("Not Enough Characters",
                "The game keeps chat windows for a character once it has logged in on it. Log in on each character once, then try again.");
        }
        else if (picked is not null)
        {
            Data.SharedChatCharacters = picked.Select(c => c.Key).ToList();
            Data.Save();
            OnPropertyChanged(nameof(SharedChatSummary));
            await RunGameWriteAsync("share chat windows", () => { SyncSharedChat(GameFolder!); return Task.CompletedTask; });
            RefreshBackups();
        }
    }

    [RelayCommand]
    private async Task MoveLeftoverSettingsAsync()
    {
        RefreshBackups();
        var files = LeftoverSettings;
        var addons = SavedSettings.Addons(files);
        var list = string.Join("\n", addons.Take(12).Select(a => $"•  {a}"))
            + (addons.Count > 12 ? $"\n•  and {addons.Count - 12} more" : "");
        var confirmed = files.Count > 0 && await Dialogs.ConfirmAsync("Move Aside Removed Addons' Settings",
            $"These addons aren't in your AddOns folder anymore. Move their saved settings ({files.Count} file{(files.Count == 1 ? "" : "s")}) "
            + $"to WTF\\_RemovedAddonSettings?\n\n{list}\n\nPut a file back where it was to restore it.", "Move Aside");
        if (confirmed)
        {
            await RunGameWriteAsync("move saved settings", () =>
            {
                SavedSettings.MoveAside(GameFolder!, files, DateTime.Now);
                return Task.CompletedTask;
            });
            RefreshBackups();
        }
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
    public async Task<bool> RunGameWriteAsync(string what, Func<Task> write, string? antivirusNote = null)
    {
        var done = false;
        if (GameProcess.IsRunning(GameFolder))
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
                    $"Windows Security (or your antivirus) flagged a file the launcher needed to {what} and blocked it. "
                    + "The launcher doesn't work around antivirus blocks. "
                    + (antivirusNote ?? "Mods that hook into the game are sometimes flagged even when they're harmless, but only "
                        + "you can decide to trust one: its home page has its author's notes."));
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
