using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Addons;

namespace SynastriaLauncher.App.ViewModels;

public sealed partial class AddonItemViewModel(AddonSource source, AddonsViewModel owner) : ObservableObject
{
    public AddonSource Source { get; } = source;
    public string Name => Source.Name;
    public string Repo => Source.Repo;
    public string Description => Source.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public string ByAuthor => $"by {Source.Author}";
    public string Origin => Source.Curated ? $"github.com/{Source.Repo}" : $"github.com/{Source.Repo}, added by you";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(PrimaryLabel), nameof(HasPrimary), nameof(CanReinstall), nameof(CanRemove),
        nameof(Versions), nameof(NeedsUpdate), nameof(HasUpdate), nameof(IsGood), nameof(IsAccent), nameof(IsInfo), nameof(IsBad),
        nameof(StateHint), nameof(HasEdits), nameof(EditsHint), nameof(CanUpdateAnyway), nameof(CanHold), nameof(ShowState), nameof(IsWorking))]
    public partial AddonCheck? Check { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(HasPrimary), nameof(CanReinstall), nameof(CanRemove), nameof(CanUpdateAnyway),
        nameof(ShowState), nameof(IsWorking))]
    public partial bool IsBusy { get; set; }

    // Up to Date and Not Installed go without saying, and an update has its Update button; only states that need a
    // look get a pill.
    public bool ShowState => IsBusy || State is not (AddonState.NotInstalled or AddonState.UpToDate) && !NeedsUpdate;
    public bool IsWorking => IsBusy || State is null;

    // Under the description: the libraries that come with it, or what an install brings along.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequiresLine), nameof(DescriptionTip))]
    public partial string RequiresLine { get; set; } = "";

    public bool HasRequiresLine => RequiresLine.Length > 0;
    public string? DescriptionTip => HasRequiresLine ? $"{Description}\n{RequiresLine}".Trim() : HasDescription ? Description : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideLabel))]
    public partial bool IsHidden { get; set; }

    // Held: the player keeps their own version; Update All and the update count pass it by.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldLabel), nameof(NeedsUpdate), nameof(StateLabel), nameof(HasPrimary), nameof(IsAccent),
        nameof(IsInfo), nameof(StateHint), nameof(CanUpdateAnyway), nameof(Versions), nameof(ShowState))]
    public partial bool IsHeld { get; set; }

    public AddonState? State => Check?.State;
    public bool HasUpdate => State == AddonState.UpdateAvailable;
    public bool NeedsUpdate => HasUpdate && !IsHeld;
    public bool HasEdits => Check is { HasLocalEdits: true };
    public bool IsGood => State == AddonState.UpToDate;
    public bool IsAccent => NeedsUpdate || State == AddonState.Unknown;
    public bool IsInfo => State == AddonState.Linked || HasUpdate && IsHeld;
    public bool IsBad => State is AddonState.RepoNotFound or AddonState.Failed;
    public string HoldLabel => IsHeld ? "Allow Updates" : "Hold Updates";
    public bool CanHold => State is AddonState.UpToDate or AddonState.UpdateAvailable or AddonState.Unknown || IsHeld;
    public bool CanUpdateAnyway => !IsBusy && HasUpdate && IsHeld;

    public string EditsHint => Check?.ChangedFiles is { Count: > 0 } files
        ? "Changed since the launcher installed it: " + string.Join(", ", files.Take(5).Select(Path.GetFileName))
            + (files.Count > 5 ? $" and {files.Count - 5} more" : "") + ". Updating replaces them, so the launcher asks first."
        : "";

    public string StateLabel => IsBusy ? "Installing"
        : State switch
        {
            AddonState.UpToDate => "Up to Date",
            AddonState.UpdateAvailable when IsHeld => "Update Held",
            AddonState.UpdateAvailable => "Update Available",
            AddonState.NotInstalled => "Not Installed",
            AddonState.Unknown => "Version Unknown",
            AddonState.Linked => "Linked",
            AddonState.RepoNotFound => "Repo Not Found",
            AddonState.Failed => "Check Failed",
            _ => "Checking",
        };

    public string StateHint => State switch
    {
        AddonState.UpdateAvailable when IsHeld => "You're holding updates for this addon. Update Anyway is in the menu.",
        AddonState.Unknown => "Installed some other way, so its version can't be compared. Reinstall it once to track updates.",
        AddonState.Linked => "Its folder is linked to a folder somewhere else, such as another game install, so the launcher "
            + "leaves it alone: no updates, no reinstalling or removing.",
        AddonState.Failed => Check?.Error ?? "",
        AddonState.RepoNotFound => "GitHub has no public repo by this name anymore.",
        _ => "",
    };

    public string Versions => Check is null ? ""
        : HasUpdate ? $"{Check.LocalVersion ?? "Installed"}  →  {Check.LatestLabel}"
        : State == AddonState.NotInstalled ? (Check.LatestLabel.Length > 0 ? $"Latest {Check.LatestLabel}" : "")
        : Check.LocalVersion ?? Check.LatestLabel;

    public string PrimaryLabel => State == AddonState.NotInstalled ? "Install" : "Update";
    public bool HasPrimary => !IsBusy && (State == AddonState.NotInstalled || NeedsUpdate);
    public bool CanReinstall => !IsBusy && State is AddonState.UpToDate or AddonState.Unknown;
    public bool CanRemove => !IsBusy && State is AddonState.UpToDate or AddonState.UpdateAvailable or AddonState.Unknown;
    public string HideLabel => !Source.Curated ? "Remove from List" : IsHidden ? "Show on the List" : "Hide from the List";

    [RelayCommand]
    private Task InstallAsync() => owner.InstallAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);

    [RelayCommand]
    private void ToggleHidden() => owner.ToggleHidden(this);

    [RelayCommand]
    private void ToggleHeld() => owner.ToggleHeld(this);

    [RelayCommand]
    private void OpenPage() => GameProcess.OpenUrl($"https://github.com/{Repo}");
}

public sealed partial class AddonSectionViewModel(string title, IReadOnlyList<AddonItemViewModel> items, AddonsViewModel owner)
    : ObservableObject
{
    public string Title { get; } = title;
    public IReadOnlyList<AddonItemViewModel> Items { get; } = items;
    public string Count => Items.Count.ToString();
    public bool IsExpanded => owner.IsExpanded(Title);
    public string ToggleHint => IsExpanded ? "Fold this section" : "Show this section";

    [RelayCommand]
    private void Toggle()
    {
        owner.ToggleSection(Title);
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ToggleHint));
    }
}

public sealed partial class AddonsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<AddonItemViewModel> Items { get; } = [];
    public ObservableCollection<AddonSectionViewModel> Sections { get; } = [];

    // Not installed yet: shown as cards under the list.
    [ObservableProperty]
    public partial AddonSectionViewModel? AvailableSection { get; set; }

    private string SectionKey(string title) => $"Addons/{title}";

    public bool IsExpanded(string title) => !main.Data.CollapsedSections.Contains(SectionKey(title));

    public void ToggleSection(string title)
    {
        if (!main.Data.CollapsedSections.Remove(SectionKey(title)))
        {
            main.Data.CollapsedSections.Add(SectionKey(title));
        }
        main.Data.Save();
    }

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckLabel), nameof(CanCheck))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingList { get; set; }

    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    [ObservableProperty]
    public partial string NewRepo { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates), nameof(UpdateAllLabel))]
    public partial int UpdateCount { get; set; }

    public bool HasUpdates => UpdateCount > 0;
    public string UpdateAllLabel => $"Update All ({UpdateCount})";
    public string CheckLabel => IsChecking ? "Checking..." : "Check for Updates";
    public bool HasGame => main.HasGameFolder;
    public int HiddenCount => Items.Count(i => i.IsHidden);
    public string ShowHiddenLabel => $"Show Hidden ({HiddenCount})";

    partial void OnShowHiddenChanged(bool value) => RefreshVisible();
    partial void OnFilterChanged(string value) => RefreshVisible();

    private string? AddOnsPath => main.GameFolder is null ? null : GameFolder.AddOnsPath(main.GameFolder);

    private AddonItemViewModel? Find(string repo) => Items.FirstOrDefault(i => LauncherData.SameRepo(i.Repo, repo));

    private AddonState? StateOf(string repo) => Find(repo)?.State;

    private bool Matches(AddonItemViewModel item)
    {
        var filter = Filter.Trim();
        return filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Repo.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private string RequiresLine(AddonItemViewModel item)
    {
        var missing = AddonDependencies.Missing(item.Source, StateOf);
        var libraries = (item.Source.Requires ?? []).Select(Find).Where(r => r is { Source.Library: true }).ToList();
        return item.State == AddonState.NotInstalled && missing.Count > 0
            ? "Also installs " + string.Join(" and ", missing.Select(r => Find(r)?.Name ?? r))
            : libraries.Count > 0 ? "Includes " + string.Join(" and ", libraries.Select(r => r!.Name))
            : "";
    }

    private void RefreshVisible()
    {
        var all = Items.Select(i => (i.Source, i.State)).ToList();
        var shown = Items.Where(i => (ShowHidden || !i.IsHidden) && Matches(i)
            && !AddonDependencies.IsFolded(i.Source, i.State, i.NeedsUpdate, all)).ToList();
        foreach (var item in Items)
        {
            item.RequiresLine = RequiresLine(item);
        }
        (string Title, Func<AddonItemViewModel, bool> Match)[] sections =
        [
            ("Updates Available", i => i.NeedsUpdate),
            ("Checking", i => i.State is null),
            ("Installed", i => i.State is AddonState.UpToDate or AddonState.Unknown || i.HasUpdate && i.IsHeld),
            ("Linked Folders", i => i.State == AddonState.Linked),
            ("Problems", i => i.State is AddonState.RepoNotFound or AddonState.Failed),
        ];
        Sections.Clear();
        foreach (var (title, match) in sections)
        {
            var items = shown.Where(match).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count > 0)
            {
                Sections.Add(new AddonSectionViewModel(title, items, this));
            }
        }
        var available = shown.Where(i => i.State == AddonState.NotInstalled).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AvailableSection = available.Count > 0 ? new AddonSectionViewModel("Available to Install", available, this) : null;
        UpdateCount = Items.Count(i => !i.IsHidden && i.NeedsUpdate);
        var installed = Items.Count(i => !i.Source.Library && AddonDependencies.IsInstalled(i.State));
        Summary = IsChecking ? "Checking GitHub for Updates"
            : $"{installed} Installed   ·   {StatusText.Updates(UpdateCount)}";
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(ShowHiddenLabel));
        OnPropertyChanged(nameof(HasGame));
        main.Play.Refresh();
    }

    private IReadOnlyList<AddonSource> _curated = AddonCatalog.Embedded().Sources;

    // Each check also picks up the curated list as published now.
    [RelayCommand]
    public async Task CheckAllAsync()
    {
        var path = AddOnsPath;
        Items.Clear();
        IsChecking = true;
        IsLoadingList = true;
        Summary = "Loading the Addon List";
        _curated = (await AddonCatalog.LoadAsync(main.GitHub, Path.Combine(LauncherData.DefaultFolder, "addons.json"),
            SelfUpdate.CurrentVersion)).Sources;
        foreach (var source in main.Data.AddonSources(_curated))
        {
            Items.Add(new AddonItemViewModel(source, this)
            {
                IsHidden = main.Data.HiddenRepos.Any(h => LauncherData.SameRepo(h, source.Repo)),
                IsHeld = main.Data.HeldRepos.Any(h => LauncherData.SameRepo(h, source.Repo)),
            });
        }
        IsChecking = path is not null;
        IsLoadingList = false;
        RefreshVisible();
        if (path is not null)
        {
            var checking = Items.ToList();
            var checks = await AddonChecker.CheckAllAsync(main.GitHub, checking.Select(i => i.Source), path, main.Data.Installed);
            foreach (var (item, check) in checking.Zip(checks))
            {
                item.Check = check;
            }
            IsChecking = false;
        }
        RefreshVisible();
    }

    private async Task RecheckAsync(AddonItemViewModel item)
    {
        item.Check = await AddonChecker.CheckAsync(main.GitHub, item.Source, AddOnsPath!, main.Data.Installed.GetValueOrDefault(item.Repo));
        RefreshVisible();
    }

    private string BackupNote => main.Data.KeepAddonBackups
        ? "The current copy is kept as a .bak folder."
        : "Turn on Keep the Previous Version in Settings first if you want a copy kept.";

    // An addon the player has edited is never replaced without asking.
    public async Task InstallAsync(AddonItemViewModel item)
    {
        var go = !item.HasEdits || await main.Dialogs.ConfirmAsync($"Replace Your Changes to {item.Name}?",
            $"{item.EditsHint}\n\n{BackupNote}", "Replace Them", "Keep Mine");
        if (go)
        {
            await DoInstallAsync(item);
        }
    }

    // Required addons that aren't installed go in first. An addon already being installed (from Update All and a
    // click, say) is left to that install.
    private async Task DoInstallAsync(AddonItemViewModel item)
    {
        if (!item.IsBusy && item.Check is { } check)
        {
            item.IsBusy = true;
            OnPropertyChanged(nameof(CanCheck));
            try
            {
                foreach (var required in AddonDependencies.Missing(item.Source, StateOf).Select(Find))
                {
                    if (required is not null)
                    {
                        await DoInstallAsync(required);
                    }
                }
                var record = await AddonInstaller.InstallAsync(main.GitHub, check, AddOnsPath!, main.Data.KeepAddonBackups);
                main.Data.Installed[item.Repo] = record with { Name = item.Name };
                main.Data.Save();
            }
            catch (OperationCanceledException)
            {
                await main.Dialogs.AlertAsync($"Couldn't Install {item.Name}", "GitHub took too long to answer. Try again in a moment.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await main.Dialogs.AlertAsync($"Couldn't Install {item.Name}", e.Message);
            }
            finally
            {
                item.IsBusy = false;
                OnPropertyChanged(nameof(CanCheck));
            }
            await RecheckAsync(item);
        }
    }

    // Not while an addon installs: a check rebuilds the list, which would offer the same install again.
    public bool CanCheck => !IsChecking && !Items.Any(i => i.IsBusy);

    // Held addons are passed by; edited ones are asked about once, together. Each is looked at again when its turn
    // comes, since a click may have updated it meanwhile.
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var updates = Items.Where(i => !i.IsHidden && i.NeedsUpdate).ToList();
        var edited = updates.Where(i => i.HasEdits).ToList();
        var replaceEdited = edited.Count > 0 && await main.Dialogs.ConfirmAsync("Some Addons Have Your Changes",
            $"{string.Join(", ", edited.Select(i => i.Name))} {(edited.Count == 1 ? "has" : "have")} files you changed since installing. "
            + $"Updating replaces them. {BackupNote}\n\nTo keep your version for good, choose Hold Updates in its menu.",
            "Update Them Too", "Skip Them");
        foreach (var item in updates.Where(i => !i.HasEdits || replaceEdited))
        {
            if (item.NeedsUpdate && Items.Contains(item))
            {
                await DoInstallAsync(item);
            }
        }
    }

    public void ToggleHeld(AddonItemViewModel item)
    {
        if (item.IsHeld)
        {
            main.Data.HeldRepos.RemoveAll(h => LauncherData.SameRepo(h, item.Repo));
        }
        else
        {
            main.Data.HeldRepos.Add(item.Repo);
        }
        item.IsHeld = !item.IsHeld;
        main.Data.Save();
        RefreshVisible();
    }

    public async Task RemoveAsync(AddonItemViewModel item)
    {
        var folders = main.Data.Installed.GetValueOrDefault(item.Repo)?.Folders ?? item.Source.Folders;
        var confirmed = await main.Dialogs.ConfirmAsync($"Remove {item.Name}",
            $"Delete {string.Join(", ", folders)} from your AddOns folder? Its saved settings in WTF stay.", "Remove");
        if (confirmed)
        {
            try
            {
                AddonInstaller.Uninstall(AddOnsPath!, folders);
                main.Data.Installed.Remove(item.Repo);
                main.Data.Save();
            }
            catch (IOException e)
            {
                await main.Dialogs.AlertAsync($"Couldn't Remove {item.Name}", e.Message);
            }
            await RecheckAsync(item);
        }
    }

    // Curated entries are hidden; ones the user added are taken off the list.
    public void ToggleHidden(AddonItemViewModel item)
    {
        if (!item.Source.Curated)
        {
            main.Data.UserAddons.RemoveAll(u => LauncherData.SameRepo(u.Repo, item.Repo));
            Items.Remove(item);
        }
        else if (item.IsHidden)
        {
            main.Data.HiddenRepos.RemoveAll(h => LauncherData.SameRepo(h, item.Repo));
            item.IsHidden = false;
        }
        else
        {
            main.Data.HiddenRepos.Add(item.Repo);
            item.IsHidden = true;
        }
        main.Data.Save();
        // The Show Hidden toggle disappears with the last hidden addon, so it mustn't stay on unseen.
        if (HiddenCount == 0)
        {
            ShowHidden = false;
        }
        RefreshVisible();
    }

    [RelayCommand]
    private void ToggleAdding() => IsAdding = !IsAdding;

    [RelayCommand]
    private async Task AddRepoAsync()
    {
        var repo = UserAddons.ParseRepo(NewRepo);
        var known = repo is not null && main.Data.AddonSources(_curated).Any(s => LauncherData.SameRepo(s.Repo, repo));
        if (repo is null)
        {
            await main.Dialogs.AlertAsync("Not a GitHub Repo", "Type it as owner/repo, or paste the repo's GitHub link.");
        }
        else if (known)
        {
            await main.Dialogs.AlertAsync("Already Listed", $"{repo} is already on the list.");
        }
        else
        {
            try
            {
                var source = await UserAddons.CreateAsync(main.GitHub, repo);
                if (source is null)
                {
                    await main.Dialogs.AlertAsync("Repo Not Found", $"GitHub has no public repo called {repo}.");
                }
                else
                {
                    main.Data.UserAddons.Add(source);
                    main.Data.Save();
                    var item = new AddonItemViewModel(source, this);
                    Items.Add(item);
                    NewRepo = "";
                    IsAdding = false;
                    await RecheckAsync(item);
                }
            }
            catch (HttpRequestException e)
            {
                await main.Dialogs.AlertAsync("Couldn't Add the Repo", e.Message);
            }
        }
    }
}
