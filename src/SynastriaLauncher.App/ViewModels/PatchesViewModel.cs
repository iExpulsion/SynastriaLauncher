using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.App.ViewModels;

public sealed partial class PatchItemViewModel(PatchDefinition patch, PatchesViewModel owner) : ObservableObject
{
    public PatchDefinition Patch { get; } = patch;
    public string Summary => Patch.Summary;
    public string Title => Patch.Title;
    public string Description => Patch.Description;
    public bool Experimental => Patch.Experimental;
    public bool HasDistance => Patch.Id == PatchIds.NameplateDistance;
    public bool HasSpacing => Patch.Id == PatchIds.NameplateStack;
    public bool HasColors => Patch.Id == PatchIds.DamageColors;
    public bool HasFolder => Patch.Id == PatchIds.DllLoader;
    public PatchesViewModel Owner { get; } = owner;

    public bool HasSettings => HasDistance || HasSpacing || HasColors || HasFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(IsAvailable), nameof(IsGood), nameof(IsPartial), nameof(IsBad), nameof(ShowStatus))]
    public partial PatchStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeLabel), nameof(HasChange), nameof(ShowStatus), nameof(DependentWarning), nameof(HasDependentWarning))]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool WasOn { get; set; }
    public bool IsAvailable => Status != PatchStatus.Unavailable || WasOn;
    public bool IsGood => Status == PatchStatus.Applied;
    public bool IsPartial => Status == PatchStatus.PartlyApplied;
    public bool IsBad => Status == PatchStatus.Unavailable || SkippedReason.Length > 0;

    // Why SynastriaLauncher.dll left this patch out the last time the game started; empty when it didn't.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(IsBad), nameof(ShowStatus))]
    public partial string SkippedReason { get; set; } = "";

    public string StatusLabel => SkippedReason.Length > 0 ? "Skipped at Last Start" : Status switch
    {
        PatchStatus.Applied => "On",
        PatchStatus.PartlyApplied => "Partly On",
        PatchStatus.Unavailable => "Doesn't Fit Your Game",
        _ => "Off",
    };

    public string StatusHint => SkippedReason.Length > 0
        ? $"The game's code didn't match this patch when it last started, so it was left out: {SkippedReason}"
        : Description;

    public bool HasChange => IsOn != WasOn;
    public bool ShowStatus => !HasChange && (IsPartial || IsBad);
    public string ChangeLabel => !HasChange ? "" : IsOn ? "Will Turn On" : "Will Turn Off";

    // Installed mods that need this patch: a quiet note while it's on, a warning once it's switched off.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedByLine), nameof(HasUsedBy), nameof(DependentWarning), nameof(HasDependentWarning))]
    public partial IReadOnlyList<string> UsedBy { get; set; } = [];

    public string Credit => Patch.Credit ?? "";
    public bool HasCredit => Credit.Length > 0;
    public string UsedByLine => UsedBy.Count == 0 ? "" : $"Used by {string.Join(", ", UsedBy)}";
    public bool HasUsedBy => UsedBy.Count > 0;
    public string DependentWarning => IsOn || UsedBy.Count == 0 ? ""
        : $"{string.Join(", ", UsedBy)} {(UsedBy.Count == 1 ? "stops" : "stop")} working while this is off.";
    public bool HasDependentWarning => DependentWarning.Length > 0;

    public string SettingSummary => Patch.Id switch
    {
        PatchIds.NameplateDistance => $"{Owner.Distance:0} Yards",
        PatchIds.NameplateStack => $"{Owner.Spacing:0}% Spacing",
        PatchIds.DamageColors => Owner.ColorTiers.Count == 1 ? "1 Color Tier" : $"{Owner.ColorTiers.Count} Color Tiers",
        PatchIds.DllLoader => Owner.DllFolderText.Trim() + "\\",
        _ => "",
    };

    public void RefreshSetting() => OnPropertyChanged(nameof(SettingSummary));

    partial void OnIsOnChanged(bool value) => Owner.Switched(this);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public sealed partial class PatchGroupViewModel(string name, IReadOnlyList<PatchItemViewModel> items) : ObservableObject
{
    public string Name { get; } = name;
    public IReadOnlyList<PatchItemViewModel> Items { get; } = items;
    public string OnLabel => $"{Items.Count(i => i.WasOn)} of {Items.Count} On";

    public void Refresh() => OnPropertyChanged(nameof(OnLabel));
}

public sealed partial class ColorTierViewModel(PatchesViewModel owner) : ObservableObject
{
    [ObservableProperty]
    public partial string Amount { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch))]
    public partial string Color { get; set; } = "";

    public IBrush Swatch => Avalonia.Media.Color.TryParse("#" + Color.TrimStart('#'), out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

    partial void OnAmountChanged(string value) => owner.UpdateSummary();
    partial void OnColorChanged(string value) => owner.UpdateSummary();

    [RelayCommand]
    private void Remove() => owner.RemoveTier(this);
}

public sealed partial class PatchesViewModel(MainViewModel main) : ObservableObject
{
    private static readonly (PatchGroup Group, string Name)[] GroupNames =
    [
        (PatchGroup.QualityOfLife, "Quality of Life"),
        (PatchGroup.Visuals, "Visuals"),
        (PatchGroup.CombatText, "Combat Text"),
        (PatchGroup.Nameplates, "Nameplates"),
        (PatchGroup.Advanced, "Advanced"),
    ];

    private CurrentSettings _loaded = new(null, null, null, null);

    public ObservableCollection<PatchGroupViewModel> Groups { get; } = [];
    public ObservableCollection<ColorTierViewModel> ColorTiers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistanceLabel))]
    public partial double Distance { get; set; } = PatchSettings.DefaultNameplateDistance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpacingLabel))]
    public partial double Spacing { get; set; } = PatchSettings.DefaultStackSpacing;

    [ObservableProperty]
    public partial string DllFolderText { get; set; } = DllFolder.Default;

    [ObservableProperty]
    public partial string PendingSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial string Problem { get; set; } = "";

    [ObservableProperty]
    public partial bool HasBackup { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    public string DistanceLabel => $"{Distance:0} yards (recommended {PatchSettings.DefaultNameplateDistance}; the game's own is 41)";
    public string SpacingLabel => $"{Spacing:0}% of a nameplate's height between stacked plates (recommended {PatchSettings.DefaultStackSpacing})";
    public bool CanAddTier => ColorTiers.Count < DamageColors.MaxTiers;

    private IEnumerable<PatchItemViewModel> Items => Groups.SelectMany(g => g.Items);

    partial void OnDistanceChanged(double value) => UpdateSummary();
    partial void OnSpacingChanged(double value) => UpdateSummary();
    partial void OnDllFolderTextChanged(string value) => UpdateSummary();

    // An exe still patched the old way, an older DLL Loader, or a missing or outdated SynastriaLauncher.dll: Apply
    // brings it up to date even when no switch changed.
    [ObservableProperty]
    public partial bool NeedsUpdate { get; set; }

    private bool _switching;

    public void Load()
    {
        var exe = main.Exe;
        Groups.Clear();
        IsLoaded = exe is not null;
        if (exe is not null)
        {
            var bytes = exe.Read();
            var items = LoadItems(exe, bytes);
            foreach (var (group, name) in GroupNames)
            {
                Groups.Add(new PatchGroupViewModel(name, items.Where(i => i.Patch.Group == group).ToList()));
            }
            HasBackup = exe.HasBackup;
            ResetSettings(_loaded);
        }
        UpdateSummary();
    }

    // Fit is judged on the exe the DLL will patch. Switches and settings come from SynastriaLauncher.ini, or, before
    // the first in-memory Apply, from the patches still written in the exe.
    private List<PatchItemViewModel> LoadItems(GameExe exe, byte[] bytes)
    {
        var baseBytes = exe.BaseBytes();
        var config = exe.ReadConfig();
        var onDisk = GameExe.HasDiskPatches(bytes);
        var diskOn = GameExe.AppliedIds(bytes);
        var loaderOn = ExePatcher.Plan(bytes, PatchIds.DllLoader, PatchSettings.Keep).Status == PatchStatus.Applied
            || GameExe.HasOlderLoader(bytes);
        var run = exe.ReadRun();
        var exeSettings = ExePatcher.ReadSettings(bytes);
        _loaded = config is null
            ? exeSettings
            : new CurrentSettings(config.NameplateDistance, config.StackSpacing, config.DamageColors, exeSettings.DllFolder);
        var items = PatchCatalog.All.Where(p => !p.Hidden).Select(p =>
        {
            var fits = p.MemoryOnly || ExePatcher.Plan(baseBytes, p, PatchSettings.Keep).Status != PatchStatus.Unavailable;
            var on = p.Id == PatchIds.DllLoader ? loaderOn
                : config?.On.Contains(p.Id) ?? diskOn.Contains(p.Id);
            var skipped = run?.Patches.GetValueOrDefault(p.Id) is { Outcome: PatcherOutcome.Skipped } result ? result.Detail : "";
            return new PatchItemViewModel(p, this)
            {
                Status = !fits ? PatchStatus.Unavailable : on ? PatchStatus.Applied : PatchStatus.NotApplied,
                SkippedReason = on && !onDisk ? skipped : "",
                WasOn = on,
                IsOn = on,
            };
        }).ToList();
        var anyOn = items.Any(i => i.IsOn && PatchCatalog.InMemory(i.Patch.Id));
        NeedsUpdate = onDisk || anyOn && !exe.HasPatcherDll(PatcherConfig.EmbeddedDll());
        return items;
    }

    // Patches applied in memory need the DLL Loader: switching one on switches it on, and switching the loader off
    // switches them off.
    public void Switched(PatchItemViewModel item)
    {
        if (!_switching)
        {
            _switching = true;
            var memoryOn = PatchCatalog.InMemory(item.Patch.Id) && item.IsOn;
            var loaderOff = item.Patch.Id == PatchIds.DllLoader && !item.IsOn;
            foreach (var other in Items.Where(i => memoryOn && i.Patch.Id == PatchIds.DllLoader
                || loaderOff && PatchCatalog.InMemory(i.Patch.Id)))
            {
                other.IsOn = memoryOn;
            }
            _switching = false;
        }
        UpdateSummary();
    }

    private void ResetSettings(CurrentSettings settings)
    {
        Distance = settings.NameplateDistance is { } d && d != 41 ? d : PatchSettings.DefaultNameplateDistance;
        Spacing = settings.StackSpacing ?? PatchSettings.DefaultStackSpacing;
        DllFolderText = settings.DllFolder ?? DllFolder.Default;
        ColorTiers.Clear();
        foreach (var tier in (settings.DamageColors ?? DamageColors.Default).OrderBy(t => t.Amount))
        {
            ColorTiers.Add(new ColorTierViewModel(this) { Amount = DamageColors.FormatAmount(tier.Amount), Color = $"{tier.Rgb:X6}" });
        }
        OnPropertyChanged(nameof(CanAddTier));
    }

    private IReadOnlyList<DamageColorTier> ParseTiers(out IReadOnlyList<string> errors) =>
        DamageColors.Parse(string.Join(",", ColorTiers.Select(t => $"{t.Amount.Trim()}={t.Color.Trim()}")), out errors);

    private bool IsOn(string id) => Items.Any(i => i.Patch.Id == id && i.IsOn);

    public void UpdateSummary()
    {
        var turnOn = Items.Count(i => i.IsOn && !i.WasOn);
        var turnOff = Items.Count(i => !i.IsOn && i.WasOn);
        var tiers = ParseTiers(out var tierErrors);
        var folder = DllFolder.Normalize(DllFolderText);
        var changed = new[]
        {
            IsOn(PatchIds.NameplateDistance) && (int)Distance != _loaded.NameplateDistance,
            IsOn(PatchIds.NameplateStack) && (int)Spacing != _loaded.StackSpacing,
            IsOn(PatchIds.DamageColors) && tierErrors.Count == 0 && !tiers.SequenceEqual(_loaded.DamageColors ?? []),
            IsOn(PatchIds.DllLoader) && folder is not null && folder != _loaded.DllFolder,
        }.Count(c => c);
        var parts = new List<string>();
        if (NeedsUpdate) parts.Add("Update How Patches Load");
        if (turnOn > 0) parts.Add($"Turn On {turnOn}");
        if (turnOff > 0) parts.Add($"Turn Off {turnOff}");
        if (changed > 0) parts.Add($"Change {changed} Setting{(changed == 1 ? "" : "s")}");
        PendingSummary = parts.Count == 0 ? "No Changes" : string.Join(", ", parts);
        PendingCount = turnOn + turnOff + changed + (NeedsUpdate ? 1 : 0);
        Problem = IsOn(PatchIds.DamageColors) && tierErrors.Count > 0 ? "Damage colors: " + string.Join("; ", tierErrors)
            : IsOn(PatchIds.DllLoader) && folder is null ? "The DLL folder must be a folder inside the game folder, like DLLs"
            : "";
        HasChanges = parts.Count > 0 && Problem.Length == 0;
        ShowFooter = parts.Count > 0 || Problem.Length > 0;
        foreach (var group in Groups)
        {
            group.Refresh();
        }
        foreach (var item in Items)
        {
            item.RefreshSetting();
        }
        // Only the badge changes while switches and sliders move; the full bar refresh reads the exe from disk.
        main.Play.PatchChanges = PendingCount;
    }

    [ObservableProperty]
    public partial int PendingCount { get; set; }

    [ObservableProperty]
    public partial bool ShowFooter { get; set; }

    [RelayCommand]
    private void AddTier()
    {
        ColorTiers.Add(new ColorTierViewModel(this) { Amount = "10M", Color = "FFFFFF" });
        OnPropertyChanged(nameof(CanAddTier));
        UpdateSummary();
    }

    public void RemoveTier(ColorTierViewModel tier)
    {
        ColorTiers.Remove(tier);
        OnPropertyChanged(nameof(CanAddTier));
        UpdateSummary();
    }

    // Stages every patch that fits this exe with the recommended settings; Apply writes them.
    [RelayCommand]
    private void RecommendedSetup()
    {
        foreach (var item in Items.Where(i => i.Status != PatchStatus.Unavailable))
        {
            item.IsOn = true;
        }
        ResetSettings(new CurrentSettings(PatchSettings.DefaultNameplateDistance, PatchSettings.DefaultStackSpacing,
            DamageColors.Default, DllFolder.Default));
        UpdateSummary();
    }

    [RelayCommand]
    private void Discard() => Load();

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private async Task ApplyAsync()
    {
        var exe = main.Exe!;
        var on = Items.Where(i => i.IsOn).Select(i => i.Patch.Id).ToList();
        var tiers = ParseTiers(out _);
        var folder = DllFolder.Normalize(DllFolderText);
        var config = new PatcherConfig(on.Where(PatchCatalog.InMemory).ToList(), (int)Distance, (int)Spacing,
            tiers.Count > 0 ? tiers : DamageColors.Default);
        StateResult? result = null;
        await main.RunGameWriteAsync("apply the patches", () =>
        {
            result = exe.ApplyMemory(config, on.Contains(PatchIds.DllLoader), folder, PatcherConfig.EmbeddedDll());
            return Task.CompletedTask;
        });
        if (result is { Result.Failed.Count: > 0 })
        {
            await main.Dialogs.AlertAsync("Can't Apply These Patches",
                string.Join("\n", result.Result.Failed.Select(f => $"{f.Patch.Title}: {string.Join("; ", f.Problems)}")) +
                "\n\nNothing was changed.");
        }
        Load();
        main.Mods.Refresh();
        main.Play.Refresh();
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var exe = main.Exe!;
        var confirmed = await main.Dialogs.ConfirmAsync("Restore Original Game",
            "Put back the original WowExt.exe saved before the first patch? Every patch is removed. Mods and addons stay.",
            "Restore Original");
        if (confirmed && await main.RunGameWriteAsync("restore WowExt.exe", () => { exe.Restore(); return Task.CompletedTask; }))
        {
            Load();
            main.Mods.Refresh();
            main.Play.Refresh();
        }
    }

    public IReadOnlyList<string> AppliedIds() => Items.Where(i => i.WasOn).Select(i => i.Patch.Id).ToList();

    public void RefreshDependents()
    {
        foreach (var item in Items)
        {
            item.UsedBy = main.Mods.ModsUsing(item.Patch.Id).Concat(main.Reforged.Using(item.Patch.Id)).ToList();
        }
    }

    // Switches a patch on without applying it, for the Mods page's Turn On; Apply still writes it.
    public void Stage(string id)
    {
        foreach (var item in Items.Where(i => i.Patch.Id == id && i.IsAvailable))
        {
            item.IsOn = true;
        }
    }
}
