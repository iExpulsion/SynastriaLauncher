using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Client;
using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.App.ViewModels;

// Synastria's client files (WowExt): checks synastria.org's download link for a new version and installs it.
public sealed partial class WowExtViewModel(MainViewModel main) : ObservableObject
{
    private static string PackageFolder => Path.Combine(LauncherData.DefaultFolder, "wowext");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateLabel))]
    public partial WowExtRelease? Available { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;
    public bool HasUpdate => Available is not null;
    public string UpdateLabel => Available is null ? "" : $"WowExt v{Available.Version} Available";
    public string Installed => main.Data.WowExtVersion is { } v ? $"WowExt v{v}" : "WowExt";

    private static string PackagePath(int version) => Path.Combine(PackageFolder, $"WoWExt_v{version}.zip");

    // A version the launcher hasn't seen installed yet is downloaded once and compared with the game's files, so a
    // client that already has it is recorded quietly.
    [RelayCommand]
    public async Task CheckAsync()
    {
        var game = main.GameFolder;
        if (game is not null && !IsBusy)
        {
            IsBusy = true;
            Status = "Checking for WowExt Updates";
            try
            {
                var release = await WowExtUpdate.CheckAsync(main.GitHub);
                var known = main.Data.WowExtVersion;
                if (release is null)
                {
                    Status = "No WowExt Download Found on synastria.org";
                }
                else if (known is { } v && v >= release.Version)
                {
                    Available = null;
                    Status = "Up to Date";
                }
                else if (known is null && (await PlanAsync(release, game)).IsCurrent)
                {
                    File.Delete(PackagePath(release.Version));
                    Record(release.Version);
                    Available = null;
                    Status = "Up to Date";
                }
                else
                {
                    Available = release;
                    Status = known is null ? $"Version {release.Version} Available, Your Files Differ" : $"Version {release.Version} Available";
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                Status = "Couldn't Check for WowExt Updates";
            }
            finally
            {
                IsBusy = false;
                OnPropertyChanged(nameof(Installed));
                main.Play.Refresh();
            }
        }
    }

    private async Task<WowExtPlan> PlanAsync(WowExtRelease release, string game)
    {
        var zip = PackagePath(release.Version);
        if (!File.Exists(zip))
        {
            Directory.CreateDirectory(PackageFolder);
            var part = zip + ".part";
            await WowExtUpdate.DownloadAsync(main.GitHub, release, part);
            File.Move(part, zip, overwrite: true);
        }
        return WowExtUpdate.Plan(WowExtUpdate.ReadPackage(zip), game, release.Version);
    }

    private void Record(int? version)
    {
        main.Data.WowExtVersion = version;
        main.Data.Save();
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (Available is { } release && main.GameFolder is { } game && !IsBusy)
        {
            IsBusy = true;
            Status = $"Downloading Version {release.Version}";
            try
            {
                await PlanAsync(release, game);
                await InstallAsync(PackagePath(release.Version), release.Version);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                Status = "Update Failed";
                await main.Dialogs.AlertAsync("WowExt Update Failed", e.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // For when the download page changes: a WoWExt_v<version>.zip the player downloaded themselves.
    [RelayCommand]
    private async Task InstallFromZipAsync()
    {
        var zip = await main.Dialogs.PickFileAsync("Choose the WowExt Zip", "WowExt package", "*.zip");
        if (zip is not null && main.GameFolder is not null && !IsBusy)
        {
            IsBusy = true;
            try
            {
                await InstallAsync(zip, WowExtUpdate.VersionFromName(Path.GetFileName(zip)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                await main.Dialogs.AlertAsync("Can't Install This Zip", e.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private static void OpenDownloadPage() => GameProcess.OpenUrl(WowExtUpdate.SiteUrl);

    private async Task InstallAsync(string zip, int? version)
    {
        var game = main.GameFolder!;
        var package = WowExtUpdate.ReadPackage(zip);
        var plan = WowExtUpdate.Plan(package, game, version);
        var name = version is { } v ? $"WowExt v{v}" : "this WowExt package";
        if (plan.IsCurrent)
        {
            Record(version ?? main.Data.WowExtVersion);
            Available = null;
            Status = "Up to Date";
            await main.Dialogs.AlertAsync("Already Up to Date", $"Your game already has every file from {name}.");
        }
        else
        {
            var old = main.Data.WowExtVersion;
            var files = string.Join(", ", plan.Updates.Select(f => f.Name));
            var exeNote = plan.ExeChanged ? "\n\nWowExt.exe changes too: the new one becomes the original the launcher keeps, and your patches go back on it." : "";
            var confirmed = await main.Dialogs.ConfirmAsync($"Install {name}",
                $"Replace {files} in your game folder?\n\nThe old files are kept in {WowExtUpdate.BackupFolderName}, and Settings' Clean Up can remove them later. "
                + $"Your WoWExt.bin (the server's cache) is kept.{exeNote}", "Install");
            StateResult? patches = null;
            var done = confirmed && await main.RunGameWriteAsync($"update WowExt", () =>
            {
                patches = WowExtUpdate.Apply(plan, package, game, old, PatcherConfig.EmbeddedDll());
                return Task.CompletedTask;
            });
            if (done)
            {
                Record(version);
                Available = null;
                Status = "Up to Date";
                if (zip.StartsWith(PackageFolder, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zip);
                }
                if (patches is { Result.Failed.Count: > 0 })
                {
                    await main.Dialogs.AlertAsync("Patches Didn't Fit the New WowExt",
                        string.Join("\n", patches.Result.Failed.Select(f => $"{f.Patch.Title}: {string.Join("; ", f.Problems)}"))
                        + "\n\nThe new WowExt.exe was installed without them. The Patches page shows what fits.");
                }
            }
            OnPropertyChanged(nameof(Installed));
            main.Patches.Load();
            main.Mods.Refresh();
            main.RefreshBackups();
            main.Play.Refresh();
        }
    }
}
