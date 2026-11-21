using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Mods;
using SynastriaLauncher.Core.Patching;
using SynastriaLauncher.Core.Reforged;
using SynastriaLauncher.Core.Wdm;

namespace SynastriaLauncher.App.ViewModels;

public sealed record VariantOption(string Id, string Name);

public sealed record SynastriaFix(bool Missing, string Tip);

public sealed partial class ReforgedPartViewModel(ReforgedPart part, ReforgedViewModel owner) : ObservableObject
{
    public ReforgedPart Part { get; } = part;
    public string Name => Part.Name;
    public string Description => Part.Description;
    public string Requirement => Part.Requirement ?? "";
    public bool HasRequirement => Requirement.Length > 0;
    public string Warning => Part.Warning ?? "";
    public bool HasWarning => Warning.Length > 0;
    public bool HasVariants => Part.Variants.Count > 1;
    public IReadOnlyList<VariantOption> Variants { get; } = part.Variants.Select(v => new VariantOption(v.Id, v.Name)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsGood), nameof(IsAccent), nameof(IsInfo), nameof(CanChange),
        nameof(CanChangeVariant), nameof(Details), nameof(VariantHint))]
    public partial PartStatus? Status { get; set; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial VariantOption? Variant { get; set; }

    [ObservableProperty]
    public partial string ChangeLabel { get; set; } = "";

    public bool HasChange => ChangeLabel.Length > 0;
    public bool CanChange => true;

    // A linked part's file is shared with other clients, so switching it to the other variant is left to its folder.
    public bool CanChangeVariant => Status is null or { Linked: false };
    public string VariantHint => CanChangeVariant ? "" : "Linked to a shared folder: switch the variant there, since it changes every client using it";
    public bool IsGood => Status is { State: ReforgedPartState.Current, Parked: false };
    public bool IsAccent => Status is { State: ReforgedPartState.UpdateAvailable or ReforgedPartState.Partial, Parked: false };
    public bool IsInfo => Status is { Linked: true, State: ReforgedPartState.Current, Parked: false };

    // A part that needs a Synastria fix to work there: Spell Particle Effects (patch-V.mpq) and Interface (the
    // Project Reforged Fix mod). Amber when the fix is missing.
    private SynastriaFix? Fix => owner.FixFor(this);
    public bool HasFix => Fix is not null;
    public string FixLabel => Fix?.Missing == true ? "Synastria Fix Missing" : "Synastria Fix";
    public string FixTip => Fix?.Tip ?? "";
    public bool FixMissing => Fix?.Missing == true;

    public string StateLabel => Status switch
    {
        null => "Checking",
        { Parked: true, Linked: true } => "Linked   ·   Switched Off",
        { Parked: true } => "Switched Off",
        { Linked: true, State: ReforgedPartState.Current } => "Linked   ·   Up to Date",
        { Linked: true } => "Linked   ·   Update Available",
        { State: ReforgedPartState.Current } => "Installed",
        { State: ReforgedPartState.UpdateAvailable } => "Update Available",
        { State: ReforgedPartState.Partial } => "Incomplete",
        _ => "Not Installed",
    };

    public string Details => owner.DetailsFor(this);

    public string? SelectedVariantId => IsOn ? Variant?.Id ?? Part.Variants[0].Id : null;

    partial void OnIsOnChanged(bool value) => owner.SelectionChanged();
    partial void OnVariantChanged(VariantOption? value) => owner.SelectionChanged();
    partial void OnChangeLabelChanged(string value) => OnPropertyChanged(nameof(HasChange));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasFix));
        OnPropertyChanged(nameof(FixLabel));
        OnPropertyChanged(nameof(FixTip));
        OnPropertyChanged(nameof(FixMissing));
    }
}

public sealed record ReforgedSectionViewModel(string Title, IReadOnlyList<ReforgedPartViewModel> Parts);

// The Project Reforged page: installs, updates and removes Reforged's patches from its official downloads.
public sealed partial class ReforgedViewModel(MainViewModel main) : ObservableObject
{
    private ReforgedStatus? _status;
    private bool _loading;
    private CancellationTokenSource? _cancel;

    public ObservableCollection<ReforgedSectionViewModel> Sections { get; } = [];
    private IEnumerable<ReforgedPartViewModel> Parts => Sections.SelectMany(s => s.Parts);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = "";

    [ObservableProperty]
    public partial double BusyPercent { get; set; }

    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string PlanSummary { get; set; } = "";

    [ObservableProperty]
    public partial string Problem { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool CanApply { get; set; }

    [ObservableProperty]
    public partial bool ShowFooter { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatchWarning))]
    public partial string PatchWarning { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatchNote))]
    public partial string PatchNote { get; set; } = "";

    public bool HasPatchWarning => PatchWarning.Length > 0;
    public bool HasPatchNote => PatchNote.Length > 0;

    // For the Play bar: installed parts whose required patch is off.
    public string InstalledWarning { get; private set; } = "";

    private IReadOnlyList<string> _missingRequired = [];
    private IReadOnlyList<string> _missingRecommended = [];
    private IReadOnlyList<ReforgedPart> _installed = [];

    public bool IsIdle => !IsBusy;
    public bool HasGame => main.HasGameFolder;

    // Another client was chosen: what was checked belongs to the old one, so the page checks again when it opens.
    public void GameFolderChanged()
    {
        _status = null;
        IsLoaded = false;
        Sections.Clear();
        Summary = "";
        ShowFooter = false;
        OnPropertyChanged(nameof(HasGame));
    }

    private static string PatchTitle(string id) => PatchCatalog.All.FirstOrDefault(p => p.Id == id)?.Title ?? id;

    private static string Requirement(IReadOnlyList<(string Patch, IReadOnlyList<ReforgedPart> Parts)> missing) =>
        string.Join("; ", missing.Select(m =>
            $"{string.Join(" and ", m.Parts.Select(p => p.Name))} {(m.Parts.Count == 1 ? "needs" : "need")} the {PatchTitle(m.Patch)} patch, which is off"));

    // The page warns about the parts chosen (installed or about to be); the Play bar and the Patches page about
    // the installed ones. Patches count once they're written to the game.
    public void RefreshPatchNeeds()
    {
        var applied = main.Patches.AppliedIds();
        _installed = main.GameFolder is { } game ? ReforgedCatalog.InstalledParts(game) : [];
        var chosen = IsLoaded ? Parts.Where(p => p.IsOn).Select(p => p.Part).ToList() : _installed;
        var (required, recommended) = ReforgedCatalog.MissingPatches(chosen, applied);
        _missingRequired = required.Select(r => r.Patch).ToList();
        _missingRecommended = recommended;
        PatchWarning = Requirement(required);
        PatchNote = recommended.Count == 0 ? ""
            : $"Project Reforged also recommends the {string.Join(" and ", recommended.Select(PatchTitle))} patch{(recommended.Count == 1 ? "" : "es")}" +
              ", so your character doesn't turn see-through up close.";
        InstalledWarning = Requirement(ReforgedCatalog.MissingPatches(_installed, applied).Required);
        var wdmAddon = main.GameFolder is { } folder && WdmService.IsInstalled(WdmCatalog.Get(WdmCatalog.Addon), folder, WdmCatalog.Locale(folder));
        MapsNote = chosen.Any(p => p.Id == "maps") && !wdmAddon
            ? "Maps and Loading Screens includes WDM's cave maps, which need the WDM addon from WoW Dungeon Maps."
            : "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMapsNote))]
    public partial string MapsNote { get; set; } = "";

    public bool HasMapsNote => MapsNote.Length > 0;

    [RelayCommand]
    private void OpenWdm() => main.Page = LauncherPage.Wdm;

    // For the Patches page's "Used by".
    public IEnumerable<string> Using(string patchId) =>
        _installed.Any(p => p.Needs.Contains(patchId)) ? ["Project Reforged"] : [];

    [RelayCommand]
    private void TurnOnRequired() => TurnOn(_missingRequired);

    [RelayCommand]
    private void TurnOnRecommended() => TurnOn(_missingRecommended);

    private void TurnOn(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            main.Patches.Stage(id);
        }
        main.Page = LauncherPage.Patches;
    }

    private ReforgedService Service => new(new ReforgedClient(), ReforgedService.DefaultCachePath);

    public string DetailsFor(ReforgedPartViewModel row)
    {
        var variant = row.Part.Variants.FirstOrDefault(v => v.Id == row.SelectedVariantId) ?? row.Part.Variants[0];
        var size = _status is null ? "" : ReforgedService.Format(_status.Size(variant));
        var version = _status?.Versions.GetValueOrDefault(variant.BucketPaths[0]) is { } v ? $"Version {v}   ·   " : "";
        var files = string.Join(", ", row.Part.Files);
        return $"{version}{size}   ·   {files}";
    }

    public SynastriaFix? FixFor(ReforgedPartViewModel row)
    {
        var game = main.GameFolder;
        SynastriaFix? fix = null;
        if (row.IsOn && game is not null && row.Part.Id == "particles")
        {
            var path = Path.Combine(ReforgedService.DataFolder(game), SpellEffectsFix.FileName);
            fix = File.Exists(path) && !SpellEffectsFix.IsOurs(path)
                ? new SynastriaFix(true, $"Your Data folder already has a {SpellEffectsFix.FileName} that the launcher didn't make, so it can't add "
                    + "its fix, and some spells show no effect. Move that file out of Data to let the launcher add it.")
                : new SynastriaFix(false, $"The launcher adds {SpellEffectsFix.FileName}, made from this part, so Reforged's spell effects and "
                    + "Synastria's own both show. It's kept up to date and goes away when this part is switched off.");
        }
        else if (row.IsOn && game is not null && row.Part.Id == "interface")
        {
            var mod = main.Mods.Items.FirstOrDefault(m => m.Mod.FollowsPart == row.Part.Id);
            fix = mod?.State == ModState.Installed
                ? new SynastriaFix(false, "The launcher adds a fix to Data\\Interface\\GlueXML so this part's character creation screen "
                    + "works on Synastria. It goes away when this part is switched off.")
                : new SynastriaFix(true, "The launcher couldn't add its fix for this part's character creation screen yet. It tries again "
                    + "the next time it starts or you press Launch with the game closed.");
        }
        return fix;
    }

    // The Mods page can change whether a fix is installed, so rows look again when this page opens.
    public void RefreshFixes()
    {
        foreach (var row in Parts)
        {
            row.Refresh();
        }
    }

    // Checks lazily, the first time the page opens: the first check hashes every installed file (~20 GB).
    public async Task EnsureLoadedAsync()
    {
        if (!IsLoaded && !_loading && main.GameFolder is not null)
        {
            await CheckAsync();
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        var game = main.GameFolder;
        if (game is not null)
        {
            _loading = true;
            using var cancel = BeginBusy(false);
            try
            {
                var progress = new Progress<ReforgedProgress>(p => ShowProgress(p, null));
                BusyText = "Checking Project Reforged's downloads...";
                _status = await Service.CheckAsync(game, progress, cancel.Token);
                Build(_status);
                IsLoaded = true;
            }
            catch (HttpRequestException e)
            {
                Summary = $"Couldn't reach Project Reforged's downloads: {e.Message}";
            }
            catch (OperationCanceledException)
            {
                Summary = "Project Reforged's downloads took too long to answer. Try Check for Updates again.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Summary = $"Couldn't read your Project Reforged files: {e.Message}";
            }
            finally
            {
                EndBusy();
                _loading = false;
            }
        }
    }

    private void Build(ReforgedStatus status)
    {
        Sections.Clear();
        var current = status.CurrentSelection();
        (ReforgedSection Section, string Title)[] titles =
            [(ReforgedSection.Core, "Core"), (ReforgedSection.Optional, "Optional"), (ReforgedSection.Audio, "Audio")];
        foreach (var (section, title) in titles)
        {
            var rows = ReforgedCatalog.Parts.Where(p => p.Section == section).Select(p =>
            {
                var row = new ReforgedPartViewModel(p, this);
                row.Status = status.Get(p.Id);
                row.Variant = row.Variants.First(v => v.Id == (status.Get(p.Id).InstalledVariant ?? p.Variants[0].Id));
                row.IsOn = current[p.Id] is not null;
                return row;
            }).ToList();
            Sections.Add(new ReforgedSectionViewModel(title, rows));
        }
        var installed = status.Parts.Count(p => p.State != ReforgedPartState.NotInstalled);
        var on = status.Parts.Count(p => p.IsOn);
        var updates = status.Parts.Count(p => p.State is ReforgedPartState.UpdateAvailable or ReforgedPartState.Partial);
        var linked = status.Parts.Count(p => p.Linked);
        Summary = installed == 0 ? "Not installed. Pick the parts you want, or start with the Core set."
            : $"{on} of {status.Parts.Count} Parts On   ·   {StatusText.Updates(updates)}"
              + (installed > on ? $"   ·   {installed - on} Switched Off, Kept for Later" : "")
              + (linked > 0 ? $"   ·   {linked} Linked to a Shared Folder" : "");
        SelectionChanged();
    }

    private Dictionary<string, string?> Selection() => Parts.ToDictionary(p => p.Part.Id, p => p.SelectedVariantId);

    public void SelectionChanged()
    {
        if (_status is not null)
        {
            var selection = Selection();
            var plan = ReforgedService.Plan(_status, selection);
            var problems = ReforgedCatalog.Problems(selection);
            foreach (var row in Parts)
            {
                var downloads = plan.Downloads.Where(d => row.Part.Files.Contains(d.File)).ToList();
                var parks = plan.Parks.Any(row.Part.Files.Contains);
                var links = plan.Links.Any(l => row.Part.Files.Contains(l.File));
                var unparks = plan.Unparks.Any(row.Part.Files.Contains);
                row.ChangeLabel = parks ? "Will Switch Off"
                    : downloads.Any(d => d.CreateLink) ? "Will Download to Shared Folder"
                    : links ? "Will Link Shared Copy"
                    : downloads.Any(d => d.ThroughLink) ? "Will Update Shared Copy"
                    : downloads.Count > 0 && row.Status?.State == ReforgedPartState.NotInstalled ? "Will Download"
                    : downloads.Count > 0 ? "Will Update"
                    : unparks ? "Will Switch On"
                    : "";
                row.Refresh();
            }
            var on = plan.Unparks.Count + plan.Links.Count;
            var parts = new List<string>();
            if (plan.Downloads.Count > 0) parts.Add($"Download {plan.Downloads.Count} File{(plan.Downloads.Count == 1 ? "" : "s")}, {ReforgedService.Format(plan.DownloadBytes)}");
            if (on > 0) parts.Add($"Switch On {on} File{(on == 1 ? "" : "s")}");
            if (plan.Parks.Count > 0) parts.Add($"Switch Off {plan.Parks.Count} File{(plan.Parks.Count == 1 ? "" : "s")}");
            PlanSummary = parts.Count == 0 ? "No Changes" : string.Join(", ", parts);
            Problem = string.Join("; ", problems);
            CanApply = !plan.IsEmpty && problems.Count == 0;
            ShowFooter = !plan.IsEmpty || problems.Count > 0;
        }
        RefreshPatchNeeds();
    }

    // Stages the Core set (HD models) on top of whatever is chosen; Apply writes it.
    [RelayCommand]
    private void CoreSet()
    {
        foreach (var row in Parts.Where(p => p.Part.DefaultOn && p.CanChange))
        {
            row.IsOn = true;
        }
    }

    [RelayCommand]
    private void Discard()
    {
        if (_status is not null)
        {
            Build(_status);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var game = main.GameFolder!;
        var selection = Selection();
        var plan = ReforgedService.Plan(_status!, selection);
        var shared = plan.Downloads.Where(d => d.ThroughLink && !d.CreateLink).Select(d => d.Target!).ToList();
        var notes = new List<string>();
        if (shared.Count > 0)
        {
            notes.Add($"Update the shared copies in {string.Join(", ", shared.Select(Path.GetDirectoryName).Distinct())}. Every client linked to them gets the update.");
        }
        var confirmed = notes.Count == 0 || await main.Dialogs.ConfirmAsync("Change Project Reforged",
            string.Join("\n\n", notes), "Go Ahead");
        if (confirmed)
        {
            var finished = false;
            await main.RunGameWriteAsync("change Project Reforged's files", async () =>
            {
                using var cancel = BeginBusy(true);
                try
                {
                    var progress = new Progress<ReforgedProgress>(p => ShowProgress(p, null));
                    await Service.ApplyAsync(game, plan, selection, progress, cancel.Token, GameProcess.IsRunning);
                    finished = true;
                    if (await Task.Run(() => SpellEffectsFix.Sync(game)) == SpellEffectsFixState.Blocked)
                    {
                        await main.Dialogs.AlertAsync("Spell Effects Fix Not Added",
                            $"Your Data folder already has a {SpellEffectsFix.FileName} that the launcher didn't make, so it left that file alone. "
                            + "Without the fix, some spells show no effect with Spell Particle Effects on Synastria.");
                    }
                }
                catch (OperationCanceledException)
                {
                    await main.Dialogs.AlertAsync("Download Stopped", "Nothing was changed. The next Apply picks up where this one stopped.");
                }
                finally
                {
                    EndBusy();
                }
            });
            if (finished)
            {
                await AfterInstallAsync();
            }
            await CheckAsync();
        }
    }

    // On Synastria, Reforged's character creation needs the Project Reforged Fix, so it follows the Interface part.
    private async Task AfterInstallAsync() => await main.Mods.SyncReforgedFixesAsync();

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    private static void OpenSite() => GameProcess.OpenUrl(ReforgedCatalog.Site);

    [RelayCommand]
    private static void OpenDiscord() => GameProcess.OpenUrl(ReforgedCatalog.Discord);

    [RelayCommand]
    private static void OpenKoFi() => GameProcess.OpenUrl(ReforgedCatalog.KoFi);

    private readonly Stopwatch _clock = new();
    private string _file = "";
    private long _fileStartBytes;

    private CancellationTokenSource BeginBusy(bool cancellable)
    {
        _cancel = new CancellationTokenSource();
        IsBusy = true;
        CanCancel = cancellable;
        BusyPercent = 0;
        _clock.Restart();
        return _cancel;
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancel = false;
        _cancel = null;
    }

    private void ShowProgress(ReforgedProgress p, string? heading)
    {
        if (p.File != _file || p.Phase != "Downloading")
        {
            _file = p.File;
            _fileStartBytes = p.Done;
            _clock.Restart();
        }
        BusyPercent = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
        var rate = (p.Done - _fileStartBytes) / Math.Max(_clock.Elapsed.TotalSeconds, 0.001) / 1e6;
        var tail = p.Phase == "Downloading" ? $"   ·   {rate:0.0} MB/s"
            : p.Phase == "Checking" ? "   ·   only the first check reads every file" : "";
        BusyText = $"{heading ?? p.Phase} {p.File} ({p.Index} of {p.Count})   ·   {ReforgedService.Format(p.Done)} of {ReforgedService.Format(p.Total)}{tail}";
    }
}
