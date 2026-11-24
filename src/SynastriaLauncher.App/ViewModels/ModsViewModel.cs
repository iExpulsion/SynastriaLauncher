using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Mods;
using SynastriaLauncher.Core.Patching;
using SynastriaLauncher.Core.Reforged;

namespace SynastriaLauncher.App.ViewModels;

public sealed partial class ModItemViewModel(ModDefinition mod, ModsViewModel owner) : ObservableObject
{
    public ModDefinition Mod { get; } = mod;
    public string Name => Mod.Name;
    public string Summary => Mod.Summary;
    public string Details => Mod.Details ?? "";
    public bool IsSynastria => Mod.IsSynastria;
    public string Warning => Mod.Warning ?? "";
    public bool HasWarning => Warning.Length > 0;
    public string Credit => Mod.IsSynastria ? $"By {Mod.Author}   ·   Included with the launcher"
        : Mod.BuiltFromSource ? $"By {Mod.Author}   ·   Version {Mod.Version}, built from source"
        : Mod.Author is null ? $"Version {Mod.Version}" : $"By {Mod.Author}   ·   Version {Mod.Version}";
    public bool HasHomepage => Mod.Homepage is not null;
    public bool CanDisable => ModInstaller.CanDisable(Mod);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(PrimaryLabel), nameof(HasPrimary), nameof(CanRemove), nameof(CanReinstall),
        nameof(ShowSwitch), nameof(IsActive), nameof(IsGood), nameof(IsAccent), nameof(ShowState), nameof(ShowUninstall), nameof(HasMenu),
        nameof(CrowdedActions))]
    public partial ModState State { get; set; }

    // Installed by the launcher at an earlier version than the list's, rather than changed by hand.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    public partial bool IsUpdate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequirement))]
    public partial string Requirement { get; set; } = "";

    // The first required patch that's off; Turn On stages it on the Patches page.
    public string? MissingPatch { get; set; }

    [ObservableProperty]
    public partial bool CanTurnOn { get; set; }

    // Required patches that are on, shown as a quiet note so players know what the mod leans on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUses))]
    public partial string Uses { get; set; } = "";

    public bool HasUses => Uses.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(HasPrimary), nameof(CanRemove), nameof(CanReinstall), nameof(ShowState),
        nameof(ShowUninstall), nameof(HasMenu), nameof(CrowdedActions))]
    public partial bool IsBusy { get; set; }

    partial void OnIsBusyChanged(bool value) => owner.BusyChanged();

    // The More menu shows only when something in it can be used.
    public bool HasMenu => CanReinstall || CanRemove && !ShowUninstall || HasHomepage;

    // Presets plus an action button fill the card's bottom row.
    public bool CrowdedActions => HasPrimary && ShowPresets;

    // The Install button already says a mod isn't installed, and the Uninstall button or Enabled switch that it is.
    public bool ShowState => IsBusy || State is not (ModState.NotInstalled or ModState.Installed);

    // Mods that can't be switched off (no DLL to park) get Uninstall in the switch's place.
    public bool ShowUninstall => CanRemove && !ShowSwitch && !HasPrimary;

    public ObservableCollection<PresetOption> Presets { get; } = [];

    private bool _settingPreset;

    [ObservableProperty]
    public partial PresetOption? SelectedPreset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrowdedActions))]
    public partial bool ShowPresets { get; set; }

    public string PresetHint => SelectedPreset?.Description ?? "";

    // Applied after the dropdown finishes its own selection change: the refresh that follows updates this list,
    // and changing it mid-selection made the dropdown read past its end and crash.
    partial void OnSelectedPresetChanged(PresetOption? oldValue, PresetOption? newValue)
    {
        OnPropertyChanged(nameof(PresetHint));
        if (!_settingPreset && newValue?.Preset is { } preset && oldValue is not null)
        {
            Dispatcher.UIThread.Post(() => _ = owner.ApplyPresetAsync(this, preset));
        }
    }

    private static readonly PresetOption Custom =
        new("Custom", "Your own edits; choosing a preset saves them as a .bak first.", null);

    // Shows the file's current preset without treating it as the player's choice. The list is built once; only the
    // Custom entry comes and goes.
    public void ShowPreset(PresetState? state)
    {
        _settingPreset = true;
        if (Presets.Count == 0)
        {
            foreach (var preset in state?.File.Presets ?? [])
            {
                Presets.Add(new PresetOption(preset.Name, preset.Description, preset));
            }
        }
        var custom = state is { IsCustom: true };
        if (custom && !Presets.Contains(Custom))
        {
            Presets.Add(Custom);
        }
        SelectedPreset = custom ? Custom : Presets.FirstOrDefault(p => p.Preset is not null && p.Preset.Id == state?.Current?.Id);
        if (!custom && Presets.Contains(Custom))
        {
            Presets.Remove(Custom);
        }
        ShowPresets = state is not null && Presets.Count > 0;
        _settingPreset = false;
        OnPropertyChanged(nameof(PresetHint));
    }

    public bool HasRequirement => Requirement.Length > 0;
    public bool IsGood => State == ModState.Installed;
    public bool IsAccent => State is ModState.OtherVersion or ModState.Partial;
    public bool ShowSwitch => CanDisable && State != ModState.NotInstalled;

    public bool IsActive
    {
        get => State != ModState.Disabled;
        set
        {
            if (value != IsActive && !IsBusy)
            {
                _ = owner.ToggleAsync(this);
            }
        }
    }

    public void ActiveChanged() => OnPropertyChanged(nameof(IsActive));

    public string StateLabel => IsBusy ? "Working" : State switch
    {
        ModState.Disabled => "Disabled",
        ModState.Installed => "Installed",
        ModState.OtherVersion when IsUpdate => "Update Available",
        ModState.OtherVersion => "Different Version",
        ModState.Partial => "Incomplete",
        _ => "Not Installed",
    };

    public string PrimaryLabel => State switch
    {
        ModState.NotInstalled => "Install",
        ModState.Partial => "Repair",
        _ when Mod.IsSynastria => "Update",
        _ => $"Update to {Mod.Version}",
    };

    public bool HasPrimary => !IsBusy && State is ModState.NotInstalled or ModState.OtherVersion or ModState.Partial;
    public bool CanReinstall => !IsBusy && State is ModState.Installed or ModState.Disabled;
    public bool CanRemove => !IsBusy && State != ModState.NotInstalled;

    [RelayCommand]
    private Task InstallAsync() => owner.InstallAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);

    [RelayCommand]
    private void OpenPage() => GameProcess.OpenUrl(Mod.Homepage!);

    [RelayCommand]
    private void TurnOnPatch() => owner.TurnOnPatch(MissingPatch);
}

public sealed record PresetOption(string Name, string Description, ModPreset? Preset);

public sealed partial class ModsViewModel(MainViewModel main) : ObservableObject
{
    private static string PatchTitle(string id) =>
        PatchCatalog.All.FirstOrDefault(p => p.Id == id)?.Title ?? id;

    public ObservableCollection<ModItemViewModel> Items { get; } = [];
    public ObservableCollection<ModItemViewModel> SynastriaItems { get; } = [];
    public ObservableCollection<ModItemViewModel> CommunityItems { get; } = [];

    [ObservableProperty]
    public partial string DllFolderNote { get; set; } = "";

    private string DllFolderName() =>
        main.Exe is { } exe ? ExePatcher.ReadSettings(exe.Read()).DllFolder ?? DllFolder.Default : DllFolder.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckLabel), nameof(CanCheck))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingList { get; set; }

    public string CheckLabel => IsChecking ? "Checking..." : "Check for Updates";

    // Not while a mod installs or uninstalls, so the list can't change under it.
    public bool CanCheck => !IsChecking && !Items.Any(i => i.IsBusy);

    public void BusyChanged() => OnPropertyChanged(nameof(CanCheck));

    // Mod versions are pinned in mods.json, so a newer published list is how mod updates arrive.
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsChecking = true;
        IsLoadingList = Items.Count == 0;
        var manifest = await ModManifest.LoadAsync(main.GitHub, Path.Combine(LauncherData.DefaultFolder, "mods.json"),
            SelfUpdate.CurrentVersion);
        Items.Clear();
        SynastriaItems.Clear();
        CommunityItems.Clear();
        foreach (var mod in manifest.Mods)
        {
            Items.Add(new ModItemViewModel(mod, this));
        }
        IsChecking = false;
        IsLoadingList = false;
        Refresh();
        _ = FetchPresetsAsync();
    }

    // A mod for something this client lacks (Project Reforged) is listed only while it's still installed. Fixes that
    // follow a Reforged part aren't listed: the part's switch on the Project Reforged page handles them.
    private void RefreshLists(string? game)
    {
        var shown = Items.Where(i => !i.Mod.FollowsReforged
            && (game is not null && ClientFeatures.Has(i.Mod.OnlyWith, game) || i.State != ModState.NotInstalled)).ToList();
        Sync(SynastriaItems, shown.Where(i => i.Mod.IsSynastria).ToList());
        Sync(CommunityItems, shown.Where(i => !i.Mod.IsSynastria).ToList());
    }

    // Rebuilding a list recreates every tile (losing scroll position and any open dropdown), so only change it when
    // what it holds changes.
    private static void Sync(ObservableCollection<ModItemViewModel> list, IReadOnlyList<ModItemViewModel> wanted)
    {
        if (!list.SequenceEqual(wanted))
        {
            list.Clear();
            foreach (var item in wanted)
            {
                list.Add(item);
            }
        }
    }

    public void Refresh()
    {
        var game = main.GameFolder;
        var dlls = DllFolderName();
        var applied = main.Patches.AppliedIds();
        DllFolderNote = $"DLL mods install into {dlls}\\ in your game folder. Switching one off parks its files in {dlls}\\{ModInstaller.DisabledFolder}\\.";
        foreach (var item in Items)
        {
            var record = main.Data.Mods.GetValueOrDefault(item.Mod.Id);
            item.State = game is null ? ModState.NotInstalled : ModInstaller.Detect(item.Mod, game, dlls, record);
            item.IsUpdate = item.State == ModState.OtherVersion && record is not null && record.Version != item.Mod.Version;
            var missing = item.Mod.Requires.Where(r => !applied.Contains(r)).ToList();
            var met = item.Mod.Requires.Where(applied.Contains).ToList();
            var unsupported = game is not null && !ClientFeatures.Has(item.Mod.OnlyWith, game);
            item.MissingPatch = missing.FirstOrDefault();
            item.Requirement = unsupported && item.State != ModState.NotInstalled
                ? "Project Reforged isn't installed anymore, so this breaks the login screen. Uninstall it."
                : missing.Count == 0 ? ""
                : $"Needs the {string.Join(" and ", missing.Select(PatchTitle))} patch, which is off";
            item.CanTurnOn = !unsupported && missing.Count > 0;
            item.Uses = met.Count == 0 ? "" : $"Uses the {string.Join(" and ", met.Select(PatchTitle))} patch";
            item.ShowPreset(game is not null && ModPresets.PresetFile(item.Mod) is { } file && item.State != ModState.NotInstalled
                ? ModPresets.Detect(file, game, dlls)
                : null);
        }
        RefreshLists(game);
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdates));
        main.Reforged.RefreshPatchNeeds();
        main.Patches.RefreshDependents();
        main.Play.Refresh();
    }

    public int UpdateCount => Items.Count(i => i.IsUpdate && !i.Mod.FollowsReforged);
    public bool HasUpdates => UpdateCount > 0;

    public void TurnOnPatch(string? id)
    {
        if (id is not null)
        {
            main.Patches.Stage(id);
        }
        main.Page = LauncherPage.Patches;
    }

    public async Task ApplyPresetAsync(ModItemViewModel item, ModPreset preset)
    {
        var game = main.GameFolder!;
        var file = ModPresets.PresetFile(item.Mod)!;
        await main.RunGameWriteAsync($"change {item.Name}'s settings", async () =>
        {
            await ModPresets.FetchAsync(main.GitHub, preset);
            ModPresets.Apply(file, preset, game, DllFolderName());
        });
        Refresh();
    }

    // Downloaded presets are fetched in the background so the dropdown can recognize them: small files every
    // time (their source may update them), presets inside an archive only once (their URL pins the version).
    private async Task FetchPresetsAsync()
    {
        var presets = Items.Select(i => ModPresets.PresetFile(i.Mod)).OfType<ModFile>()
            .SelectMany(f => f.Presets!).Where(p => p.IsDownloaded && (p.Entry is null || !ModPresets.IsCached(p))).ToList();
        foreach (var preset in presets)
        {
            try
            {
                await ModPresets.FetchAsync(main.GitHub, preset);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                // Offline or moved: the preset stays unrecognized until it can be fetched.
            }
        }
        if (presets.Count > 0)
        {
            Refresh();
        }
    }

    // Installed mods (switched on) that need the patch, for the Patches page's warnings. A fix that follows a Reforged
    // part is covered by Project Reforged's own entry.
    public IReadOnlyList<string> ModsUsing(string patchId) =>
        Items.Where(m => !m.Mod.FollowsReforged && m.State is ModState.Installed or ModState.OtherVersion or ModState.Partial
                && m.Mod.Requires.Contains(patchId))
            .Select(m => m.Name).ToList();

    public async Task InstallAsync(ModItemViewModel item)
    {
        var game = main.GameFolder!;
        item.IsBusy = true;
        try
        {
            await main.RunGameWriteAsync($"install {item.Name}", async () =>
            {
                var record = await ModInstaller.InstallAsync(main.GitHub, item.Mod, game, DllFolderName());
                main.Data.Mods[item.Mod.Id] = record;
                main.Data.Save();
            });
        }
        finally
        {
            item.IsBusy = false;
            Refresh();
        }
    }

    // The switch shows the result afterwards, whether or not the change went through.
    public async Task ToggleAsync(ModItemViewModel item)
    {
        var game = main.GameFolder!;
        var enable = item.State == ModState.Disabled;
        item.IsBusy = true;
        try
        {
            await main.RunGameWriteAsync($"{(enable ? "enable" : "disable")} {item.Name}", () =>
            {
                ModInstaller.SetEnabled(item.Mod, game, DllFolderName(), enable);
                return Task.CompletedTask;
            });
        }
        finally
        {
            item.IsBusy = false;
            Refresh();
            item.ActiveChanged();
        }
    }

    // Fixes that follow a Reforged part: installed while all of the part's files are in Data, removed otherwise.
    // Skipped while the game runs; the next launcher start or Launch catches up.
    public async Task SyncReforgedFixesAsync()
    {
        var game = main.GameFolder;
        if (game is not null && !GameProcess.IsRunning(game))
        {
            Refresh();
            foreach (var item in Items.Where(i => i.Mod.FollowsReforged))
            {
                var part = ReforgedCatalog.Get(item.Mod.FollowsPart!);
                var on = part.Files.All(f => File.Exists(Path.Combine(ReforgedService.DataFolder(game), f)));
                try
                {
                    if (on && item.State != ModState.Installed)
                    {
                        main.Data.Mods[item.Mod.Id] = await ModInstaller.InstallAsync(main.GitHub, item.Mod, game, DllFolderName());
                        main.Data.Save();
                    }
                    else if (!on && item.State != ModState.NotInstalled)
                    {
                        ModInstaller.Uninstall(item.Mod, game, DllFolderName());
                        main.Data.Mods.Remove(item.Mod.Id);
                        main.Data.Save();
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
                {
                    App.LogCrash(e);
                }
            }
            Refresh();
        }
    }

    public async Task RemoveAsync(ModItemViewModel item)
    {
        var game = main.GameFolder!;
        var confirmed = await main.Dialogs.ConfirmAsync($"Uninstall {item.Name}",
            $"Delete {item.Name}'s files from the game folder? Its settings files stay, for a later reinstall.", "Uninstall");
        if (confirmed && await main.RunGameWriteAsync($"uninstall {item.Name}", () =>
            {
                ModInstaller.Uninstall(item.Mod, game, DllFolderName());
                return Task.CompletedTask;
            }))
        {
            main.Data.Mods.Remove(item.Mod.Id);
            main.Data.Save();
        }
        Refresh();
    }
}
