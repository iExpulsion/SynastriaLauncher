using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
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
        var mods = main.Mods.Items.Count(m => m.State is ModState.Installed or ModState.OtherVersion);
        AddonUpdates = main.Addons.UpdateCount;
        PatchChanges = main.Patches.PendingCount;
        StatusTitle = !main.HasGameFolder ? "Choose Your Game Folder" : "Ready to Play";
        StatusLine = !main.HasGameFolder
            ? "Settings, Game Folder: pick the folder with WowExt.exe"
            : string.Join("   ·   ",
                Plural(patchesOn, "Patch", "Patches") + " On",
                Plural(mods, "Mod", "Mods") + " Active",
                AddonUpdates == 0 ? "Addons Up to Date" : Plural(AddonUpdates, "Addon Update", "Addon Updates"));
        main.RefreshBackups();
        Warnings = string.Join("   ", main.Mods.Items
            .Where(m => m.State is not (ModState.NotInstalled or ModState.Disabled) && m.HasRequirement)
            .Select(m => $"{m.Name}: {m.Requirement}")
            .Append(main.Reforged.InstalledWarning.Length > 0 ? $"Project Reforged's {main.Reforged.InstalledWarning}" : "")
            .Append(main.GameFolder is { } game && WdmService.ReforgedConflicts(game, WdmCatalog.Locale(game)).Count > 0
                ? "WDM's map patches replace Project Reforged's maps; uninstall them on WoW Dungeon Maps" : "")
            .Where(w => w.Length > 0));
    }

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    [RelayCommand]
    private async Task PlayAsync()
    {
        var exe = main.Exe;
        if (exe is not null && GameProcess.IsRunning())
        {
            await main.Dialogs.AlertAsync("Already Running", "The game is already running.");
        }
        else if (exe is not null && main.IsWritingGame)
        {
            await main.Dialogs.AlertAsync("Still Working", "The launcher is still changing files in the game folder. Start the game once it's done.");
        }
        else if (exe is not null)
        {
            GameProcess.Start(exe.ExePath);
        }
        else
        {
            main.Page = LauncherPage.Settings;
        }
    }
}
