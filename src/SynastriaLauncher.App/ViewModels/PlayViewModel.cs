using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core.Accounts;
using SynastriaLauncher.Core.Mods;
using SynastriaLauncher.Core.Wdm;

namespace SynastriaLauncher.App.ViewModels;

// The bar along the bottom of every page: game status, what needs attention, the navigation badges and Play.
public sealed partial class PlayViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main => main;

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = "";

    [ObservableProperty]
    public partial string StatusLine { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial string Warnings { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddonBadge))]
    public partial int AddonUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatchBadge))]
    public partial int PatchChanges { get; set; }

    public bool HasWarnings => Warnings.Length > 0;
    public bool HasAddonBadge => AddonUpdates > 0;
    public bool HasPatchBadge => PatchChanges > 0;

    public void Refresh()
    {
        var patchesOn = main.Patches.AppliedIds().Count;
        var mods = main.Mods.Items.Count(m => !m.Mod.FollowsReforged && m.State is ModState.Installed or ModState.OtherVersion);
        AddonUpdates = main.Addons.UpdateCount;
        PatchChanges = main.Patches.PendingCount;
        StatusTitle = !main.HasGameFolder ? "Choose Your Game Folder" : "Ready to Play";
        StatusLine = !main.HasGameFolder
            ? "Settings, Game Folder: pick the folder with WowExt.exe"
            : string.Join("   ·   ", new[]
            {
                Plural(patchesOn, "Patch", "Patches") + " On",
                Plural(mods, "Mod", "Mods") + " Active",
                AddonUpdates == 0 ? "Addons Up to Date" : Plural(AddonUpdates, "Addon Update", "Addon Updates"),
                main.Reforged.HasUpdates ? Plural(main.Reforged.UpdateCount, "Reforged Update", "Reforged Updates") : "",
                main.Wdm.HasUpdates ? Plural(main.Wdm.UpdateCount, "Dungeon Maps Update", "Dungeon Maps Updates") : "",
            }.Where(s => s.Length > 0));
        main.RefreshBackups();
        Warnings = string.Join("   ", main.Mods.Items
            .Where(m => !m.Mod.FollowsReforged && m.State is not (ModState.NotInstalled or ModState.Disabled) && m.HasRequirement)
            .Select(m => $"{m.Name}: {m.Requirement}")
            .Prepend(main.PatcherDllWarning)
            .Append(main.Reforged.InstalledWarning.Length > 0 ? $"Project Reforged's {main.Reforged.InstalledWarning}" : "")
            .Append(main.GameFolder is { } game && WdmService.ReforgedConflicts(game, WdmCatalog.Locale(game)).Count > 0
                ? "WDM's map patches replace Project Reforged's maps; uninstall them on WoW Dungeon Maps" : "")
            .Where(w => w.Length > 0));
    }

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    [RelayCommand]
    private Task PlayAsync() => StartGameAsync(main.Data.FindAccount(main.Data.LaunchAccount));

    // Starts the game, logged in as the account when one is given; true when it started.
    public async Task<bool> StartGameAsync(SavedAccount? account)
    {
        var exe = main.Exe;
        var started = false;
        if (exe is not null && GameProcess.IsRunning(main.GameFolder))
        {
            await main.Dialogs.AlertAsync("Already Running", "The game is already running.");
        }
        else if (exe is not null && main.IsWritingGame)
        {
            await main.Dialogs.AlertAsync("Still Working", "The launcher is still changing files in the game folder. Start the game once it's done.");
        }
        else if (exe is not null)
        {
            await main.SyncGameFilesAsync();
            await main.Mods.SyncReforgedFixesAsync();
            Refresh();
            var start = main.PatcherDllWarning.Length == 0 || await main.Dialogs.ConfirmAsync("Start Without Patches?",
                (main.PatcherDllBlocked
                    ? "Your antivirus blocked SynastriaLauncher.dll, the launcher's own patcher, so the game would start with none of your patches. "
                    : "SynastriaLauncher.dll, the launcher's own patcher, is missing from the game folder (an antivirus may have removed it), so the game would start with none of your patches. ")
                + "If you use Interface Edit, the game may then say your interface files are corrupted. "
                + (main.PatcherDllBlocked
                    ? "If you trust it, restore it from Windows Security's Protection History (or your antivirus's quarantine) and allow it."
                    : "Press Apply Changes on Patches to put it back."),
                "Start Anyway");
            var password = start && account is not null ? AccountSecrets.Unprotect(account.Password) : null;
            var login = password is not null && account is not null && LoginHandoff.CanCarry(account.Name, password, account.Character)
                ? LoginHandoff.Value(account.Name, password, account.Character) : null;
            start = start && (account is null || login is not null || await main.Dialogs.ConfirmAsync("Start Without Logging In?",
                $"The saved password for {account.Name} can't be read here: it was saved by another Windows user or on another PC. "
                + "Edit the account in Settings to enter it again.", "Start Anyway"));
            if (start)
            {
                GameProcess.StartGame(exe.ExePath, login);
                started = true;
            }
        }
        else
        {
            main.Page = LauncherPage.Settings;
        }
        return started;
    }
}
