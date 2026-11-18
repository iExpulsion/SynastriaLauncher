using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core.Wdm;

namespace SynastriaLauncher.App.ViewModels;

public sealed partial class WdmPartViewModel(WdmPart part, WdmViewModel owner) : ObservableObject
{
    public WdmPart Part { get; } = part;
    public string Name => Part.Name;
    public string Description => Part.Description;
    public bool IsPreRelease => Part.PreRelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(ShowState), nameof(IsGood), nameof(IsAccent), nameof(IsInfo), nameof(PrimaryLabel),
        nameof(HasPrimary), nameof(CanReinstall), nameof(CanUninstall), nameof(DetailsLine), nameof(LockNote), nameof(IsLocked),
        nameof(ShowUninstall))]
    public partial WdmStatus? Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(HasPrimary), nameof(CanReinstall), nameof(CanUninstall), nameof(ShowState),
        nameof(ShowUninstall))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissing), nameof(HasPrimary), nameof(ShowUninstall))]
    public partial string Missing { get; set; } = "";

    public bool HasMissing => Missing.Length > 0;
    private WdmState? State => Status?.State;
    public bool IsLocked => State == WdmState.InReforged;
    public string LockNote => IsLocked ? "Already in Project Reforged's Maps and Loading Screens, with larger art" : "";
    // Installed goes without saying next to its Uninstall button, like on Mods.
    public bool ShowState => IsBusy || State is not (null or WdmState.NotInstalled or WdmState.Installed);
    public bool ShowUninstall => CanUninstall && !HasPrimary;
    public bool IsGood => State == WdmState.Installed;
    public bool IsAccent => State is WdmState.UpdateAvailable or WdmState.VersionUnknown;
    public bool IsInfo => State is WdmState.Linked or WdmState.InReforged;

    public string StateLabel => IsBusy ? "Working" : State switch
    {
        WdmState.Installed => "Installed",
        WdmState.UpdateAvailable => "Update Available",
        WdmState.VersionUnknown => "Version Unknown",
        WdmState.Linked => "Linked",
        WdmState.InReforged => "In Project Reforged",
        _ => "",
    };

    public string PrimaryLabel => State == WdmState.NotInstalled ? "Install" : "Update";
    public bool HasPrimary => !IsBusy && Status?.LatestTag is not null && !HasMissing
        && State is WdmState.NotInstalled or WdmState.UpdateAvailable or WdmState.VersionUnknown;
    public bool CanReinstall => !IsBusy && Status?.LatestTag is not null && State == WdmState.Installed;
    public bool CanUninstall => !IsBusy && State is WdmState.Installed or WdmState.UpdateAvailable or WdmState.VersionUnknown;

    public string DetailsLine => string.Join("   ·   ", new[]
    {
        Part.Repo,
        Status?.InstalledTag is { } installed ? $"Version {installed}" : Status?.LatestTag is { } latest ? $"Latest {latest}" : null,
        Part.Kind == WdmKind.Patch ? $"Data\\{owner.Locale}\\{Part.AssetFor(Part.Assets[0], owner.Locale)}" : string.Join(", ", Part.Folders),
    }.OfType<string>());

    public string Requirement => Part.Requirement ?? "";
    public bool HasRequirement => Requirement.Length > 0;

    [RelayCommand]
    private Task InstallAsync() => owner.InstallAsync(this);

    [RelayCommand]
    private Task UninstallAsync() => owner.UninstallAsync(this);

    [RelayCommand]
    private void OpenPage() => GameProcess.OpenUrl($"https://github.com/{Part.Repo}");

    public void RefreshDetails() => OnPropertyChanged(nameof(DetailsLine));
}

public sealed record WdmSectionViewModel(string Title, string Note, IReadOnlyList<WdmPartViewModel> Parts);

// The WoW Dungeon Maps page: Trimitor's map patches and map addons, installed from their GitHub releases.
public sealed partial class WdmViewModel(MainViewModel main) : ObservableObject
{
    private readonly Dictionary<string, string?> _latest = [];

    public ObservableCollection<WdmSectionViewModel> Sections { get; } = [];
    private IEnumerable<WdmPartViewModel> Rows => Sections.SelectMany(s => s.Parts);

    public string Locale { get; private set; } = "enUS";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict))]
    public partial string Conflict { get; set; } = "";

    public bool HasConflict => Conflict.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;
    public bool HasGame => main.HasGameFolder;
    private bool _loaded;

    // Another client was chosen; the page checks it again when it opens.
    public void GameFolderChanged()
    {
        _loaded = false;
        OnPropertyChanged(nameof(HasGame));
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded && main.GameFolder is not null)
        {
            await CheckAsync();
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        var game = main.GameFolder;
        if (game is not null && !IsBusy)
        {
            IsBusy = true;
            Summary = "Checking Trimitor's Releases";
            Locale = WdmCatalog.Locale(game);
            if (Sections.Count == 0)
            {
                Build();
            }
            try
            {
                _latest.Clear();
                foreach (var part in WdmCatalog.Parts)
                {
                    var key = $"{part.Repo}|{part.PreRelease}";
                    if (!_latest.ContainsKey(key))
                    {
                        _latest[key] = await WdmService.LatestTagAsync(main.GitHub, part, Locale);
                    }
                }
                _loaded = true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Summary = $"Couldn't reach GitHub: {e.Message}";
            }
            finally
            {
                IsBusy = false;
                Refresh();
            }
        }
    }

    private void Build()
    {
        WdmPartViewModel Row(WdmPart p) => new(p, this);
        Sections.Add(new WdmSectionViewModel("Map Patches",
            $"Go into Data\\{Locale}, for your client's language. Project Reforged's Maps and Loading Screens already include both.",
            WdmCatalog.Parts.Where(p => p.Kind == WdmKind.Patch).Select(Row).ToList()));
        Sections.Add(new WdmSectionViewModel("Addons",
            "Mapster, GatherMate and QuestHelper here are Trimitor's versions for the new maps; installing one replaces any copy you have.",
            WdmCatalog.Parts.Where(p => p.Kind == WdmKind.Addon).Select(Row).ToList()));
    }

    // Reads the game folder again; the release tags come from the last check.
    public void Refresh()
    {
        var game = main.GameFolder;
        if (game is not null)
        {
            foreach (var row in Rows)
            {
                var latest = _latest.GetValueOrDefault($"{row.Part.Repo}|{row.Part.PreRelease}");
                row.Status = WdmService.Evaluate(row.Part, game, Locale, main.Data.WdmTags.GetValueOrDefault(row.Part.Id), latest);
                IReadOnlyList<WdmPart> missing = row.Status.State is WdmState.NotInstalled ? WdmService.MissingNeeds(row.Part, game, Locale) : [];
                row.Missing = missing.Count == 0 ? "" : $"Install {string.Join(" and ", missing.Select(m => m.Name))} first";
                row.RefreshDetails();
            }
            var conflicts = WdmService.ReforgedConflicts(game, Locale);
            Conflict = conflicts.Count == 0 ? ""
                : $"Project Reforged's Maps and Loading Screens already include {string.Join(" and ", conflicts.Select(c => c.Name))}, with larger art. "
                  + "WDM's copies only replace that art, so uninstall them.";
            var installed = Rows.Count(r => r.Status?.State is not (null or WdmState.NotInstalled or WdmState.InReforged));
            var updates = Rows.Count(r => r.Status?.State == WdmState.UpdateAvailable);
            Summary = !_loaded ? Summary
                : installed == 0 ? "Not installed. Start with the WDM addon, plus Dungeon Maps unless Project Reforged has them."
                : $"{installed} of {Rows.Count()} Installed   ·   {StatusText.Updates(updates)}";
        }
        OnPropertyChanged(nameof(HasGame));
    }

    public async Task InstallAsync(WdmPartViewModel row)
    {
        var game = main.GameFolder!;
        var tag = _latest.GetValueOrDefault($"{row.Part.Repo}|{row.Part.PreRelease}");
        if (tag is not null)
        {
            row.IsBusy = true;
            await main.RunGameWriteAsync($"install {row.Name}", async () =>
            {
                await WdmService.InstallAsync(main.GitHub, row.Part, tag, game, Locale, main.Data.KeepAddonBackups);
                main.Data.WdmTags[row.Part.Id] = tag;
                main.Data.Save();
            });
            row.IsBusy = false;
            AfterChange();
        }
    }

    public async Task UninstallAsync(WdmPartViewModel row)
    {
        var game = main.GameFolder!;
        var what = row.Part.Kind == WdmKind.Patch
            ? $"Delete {row.Part.AssetFor(row.Part.Assets[0], Locale)} from Data\\{Locale}?"
            : $"Delete {string.Join(", ", row.Part.Folders)} from your AddOns folder? Their saved settings in WTF stay.";
        if (await main.Dialogs.ConfirmAsync($"Uninstall {row.Name}", what, "Uninstall"))
        {
            await main.RunGameWriteAsync($"uninstall {row.Name}", () =>
            {
                WdmService.Uninstall(row.Part, game, Locale);
                main.Data.WdmTags.Remove(row.Part.Id);
                main.Data.Save();
                return Task.CompletedTask;
            });
            AfterChange();
        }
    }

    // For the conflict warning: removes WDM's map patches that Reforged's maps already cover.
    [RelayCommand]
    private async Task UninstallConflictsAsync()
    {
        var game = main.GameFolder!;
        var conflicts = WdmService.ReforgedConflicts(game, Locale);
        if (conflicts.Count > 0 && await main.Dialogs.ConfirmAsync("Uninstall WDM's Map Patches",
                $"Delete {string.Join(", ", conflicts.Select(c => c.AssetFor(c.Assets[0], Locale)))} from Data\\{Locale}? "
                + "Project Reforged's maps already include them.", "Uninstall"))
        {
            await main.RunGameWriteAsync("uninstall WDM's map patches", () =>
            {
                foreach (var part in conflicts)
                {
                    WdmService.Uninstall(part, game, Locale);
                    main.Data.WdmTags.Remove(part.Id);
                }
                main.Data.Save();
                return Task.CompletedTask;
            });
            AfterChange();
        }
    }

    private void AfterChange()
    {
        Refresh();
        main.Reforged.RefreshPatchNeeds();
        main.Play.Refresh();
    }

    [RelayCommand]
    private static void OpenPatchRepo() => GameProcess.OpenUrl($"https://github.com/{WdmCatalog.PatchRepo}");

    [RelayCommand]
    private static void OpenAddonRepo() => GameProcess.OpenUrl($"https://github.com/{WdmCatalog.AddonRepo}");
}
