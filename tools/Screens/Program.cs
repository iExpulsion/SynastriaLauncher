// Renders the launcher's pages to PNG without showing a window, against this machine's real launcher data and
// game folder (read only: nothing is applied or installed). Usage: dotnet run --project tools/Screens -- <outDir>
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SynastriaLauncher.App;
using SynastriaLauncher.App.ViewModels;

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "launcher-screens");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var window = new MainWindow { Width = 1180, Height = 760 };
window.Show();
var model = (MainViewModel)window.DataContext!;
Pump(TimeSpan.FromSeconds(10));

// --presets <unpatched WowExt.exe>: switches DXVK's preset through the real dropdown on a throwaway game folder
// and checks the file follows, instead of taking screenshots.
if (args.Length > 2 && args[1] == "--presets")
{
    var game = Directory.CreateTempSubdirectory("launcher-presets-").FullName;
    File.Copy(args[2], Path.Combine(game, "WowExt.exe"));
    Directory.CreateDirectory(Path.Combine(game, "WTF"));
    Directory.CreateDirectory(Path.Combine(game, "DLLs"));
    File.WriteAllText(Path.Combine(game, "DLLs", "d3d9.dll"), "fake");
    File.WriteAllText(Path.Combine(game, "WTF", "dxvk.conf"), "d3d9.enlargeHardwareCursor = 1\n");
    model.GameFolder = game;
    model.Patches.Load();
    var loading = model.Mods.LoadAsync();
    while (!loading.IsCompleted)
    {
        Pump(TimeSpan.FromMilliseconds(100));
    }
    model.Page = LauncherPage.Mods;
    Pump(TimeSpan.FromSeconds(1));
    foreach (var index in new[] { 2, 0, 3, 1 })
    {
        var combo = window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>()
            .First(c => c.DataContext is ModItemViewModel { Mod.Id: "dxvk" });
        combo.SelectedIndex = index;
        Pump(TimeSpan.FromSeconds(1));
        var conf = Path.Combine(game, "WTF", "dxvk.conf");
        var text = File.Exists(conf) ? File.ReadAllText(conf).Replace("\n", " | ").Trim() : "(no file)";
        Console.WriteLine($"picked {index}: {text}, shown '{model.Mods.Items.First(i => i.Mod.Id == "dxvk").SelectedPreset?.Name}'");
    }
    Directory.Delete(game, recursive: true);
    return;
}

// --needs <unpatched WowExt.exe>: a throwaway client with Reforged's Interface part and no patches, to show the
// Reforged page's patch warnings.
if (args.Length > 2 && args[1] == "--needs")
{
    var game = Directory.CreateTempSubdirectory("launcher-needs-").FullName;
    File.Copy(args[2], Path.Combine(game, "WowExt.exe"));
    Directory.CreateDirectory(Path.Combine(game, "Data"));
    File.WriteAllText(Path.Combine(game, "Data", "patch-I.mpq"), "fake");
    model.GameFolder = game;
    model.Patches.Load();
    var loading = model.Mods.LoadAsync();
    while (!loading.IsCompleted)
    {
        Pump(TimeSpan.FromMilliseconds(100));
    }
    model.Page = LauncherPage.Reforged;
    Pump(TimeSpan.FromSeconds(20));
    Shot("reforged-needs");
    Directory.Delete(game, recursive: true);
    return;
}

// --readme: the README's screenshots, made to look like a typical player's launcher rather than this machine's:
// nothing linked, a few addons not installed yet and one with an update.
if (args.Length > 1 && args[1] == "--readme")
{
    model.Page = LauncherPage.Addons;
    Pump(TimeSpan.FromSeconds(5));
    string[] toInstall = ["SynScoots/ScootsCraft", "SynScoots/ScootsLibOptions", "RedeemedRiku/LootPop", "Sablinova-WoW/Soul-Forge",
        "RosemyneH/TheJournal", "speedyrulz/AttuneNext", "SynScoots/MehTrajectory", "RosemyneH/ResourceBankViewer",
        "SynScoots/ScootsRares", "SynScoots/ScootsProgressBar"];
    foreach (var addon in model.Addons.Items.Where(i => i.Check is not null))
    {
        var state = toInstall.Contains(addon.Repo) ? SynastriaLauncher.Core.Addons.AddonState.NotInstalled
            : addon.Repo == "SynScoots/ScootsStats" ? SynastriaLauncher.Core.Addons.AddonState.UpdateAvailable
            : SynastriaLauncher.Core.Addons.AddonState.UpToDate;
        addon.Check = addon.Check! with
        {
            State = state,
            ReleaseTag = addon.Repo == "SynScoots/ScootsStats" ? "2.9.4" : addon.Check.ReleaseTag,
        };
    }
    model.Addons.ShowHidden = !model.Addons.ShowHidden;
    model.Addons.ShowHidden = !model.Addons.ShowHidden;
    model.Play.Refresh();
    Shot("addons");
    Pump(TimeSpan.FromMilliseconds(600));
    ScrollTo(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
        .First(b => b.Classes.Contains("sectionToggle") && b.DataContext == model.Addons.AvailableSection), 8);
    Shot("addons-available");

    model.Page = LauncherPage.Patches;
    Shot("patches");
    var colors = model.Patches.Groups.SelectMany(g => g.Items).First(i => i.HasColors);
    colors.IsExpanded = true;
    Pump(TimeSpan.FromMilliseconds(600));
    var shortNumbers = model.Patches.Groups.SelectMany(g => g.Items).First(i => i.Patch.Id == "ShortNumbers");
    ScrollTo(window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().First(b => b.DataContext == shortNumbers), 70);
    Shot("patches-settings");

    model.Page = LauncherPage.Mods;
    Shot("mods");

    model.Page = LauncherPage.Reforged;
    Pump(TimeSpan.FromSeconds(60));
    foreach (var part in model.Reforged.Sections.SelectMany(s => s.Parts).Where(p => p.Status is not null))
    {
        part.Status = part.Status! with { Linked = false };
        part.Refresh();
    }
    model.Reforged.Summary = model.Reforged.Summary.Split("   ·   ").Where(s => !s.Contains("Linked")).Aggregate((a, b) => $"{a}   ·   {b}");
    Shot("reforged");

    model.Page = LauncherPage.Wdm;
    Pump(TimeSpan.FromSeconds(20));
    Shot("wdm");
    return;
}

// The open page's scroll area.
Avalonia.Controls.ScrollViewer PageScroller() =>
    window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().Where(s => s.IsEffectivelyVisible)
        .OrderByDescending(s => s.Extent.Height).First();

// Scrolls the open page to its bottom.
void ScrollToEnd()
{
    Pump(TimeSpan.FromMilliseconds(600));
    var page = PageScroller();
    page.Offset = new Avalonia.Vector(0, page.Extent.Height - page.Viewport.Height);
}

// Scrolls the open page so the control sits margin pixels below the top.
void ScrollTo(Avalonia.Controls.Control control, double margin)
{
    var page = PageScroller();
    var top = control.TranslatePoint(new Avalonia.Point(0, 0), page)?.Y ?? 0;
    page.Offset = new Avalonia.Vector(0, Math.Max(0, page.Offset.Y + top - margin));
}

void Pump(TimeSpan time)
{
    var end = DateTime.UtcNow + time;
    while (DateTime.UtcNow < end)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(30);
    }
}

void Shot(string name)
{
    Pump(TimeSpan.FromMilliseconds(600));
    window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine(Path.Combine(outDir, name + ".png"));
}

foreach (var page in Enum.GetValues<LauncherPage>())
{
    model.Page = page;
    // Project Reforged checks its files the first time it opens.
    Pump(page == LauncherPage.Reforged ? TimeSpan.FromSeconds(60) : page == LauncherPage.Wdm ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
    Shot(page.ToString().ToLowerInvariant());
}

// Reforged from Interface down, for the Synastria Fix badges on Interface and Spell Particle Effects.
model.Page = LauncherPage.Reforged;
Pump(TimeSpan.FromMilliseconds(600));
var interfacePart = model.Reforged.Sections.SelectMany(s => s.Parts).First(p => p.Part.Id == "interface");
ScrollTo(window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().First(b => b.DataContext == interfacePart), 20);
Shot("reforged-optional");

// Full-height pages, then Patches with staged changes and settings open.
window.Height = 1500;
foreach (var page in new[] { LauncherPage.Addons, LauncherPage.Mods, LauncherPage.Wdm })
{
    model.Page = page;
    Shot(page.ToString().ToLowerInvariant() + "-full");
}
// Addons with a few shown as not installed or outdated, for the cards and the update row.
model.Page = LauncherPage.Addons;
string[] notInstalled = ["SynScoots/ScootsCraft", "SynScoots/ScootsLibOptions", "RedeemedRiku/LootPop", "Sablinova-WoW/Soul-Forge", "RosemyneH/TheJournal"];
foreach (var addon in model.Addons.Items.Where(i => i.Check is not null))
{
    addon.Check = notInstalled.Contains(addon.Repo) ? addon.Check! with { State = SynastriaLauncher.Core.Addons.AddonState.NotInstalled }
        : addon.Repo == "SynScoots/ScootsStats" ? addon.Check! with { State = SynastriaLauncher.Core.Addons.AddonState.UpdateAvailable, ReleaseTag = "2.9.4" }
        : addon.Check;
}
model.Addons.ShowHidden = !model.Addons.ShowHidden;
model.Addons.ShowHidden = !model.Addons.ShowHidden;
Shot("addons-available");
// Installed folded, then opened again (the launcher saves which sections are folded).
var installedSection = model.Addons.Sections.First(s => s.Title == "Installed");
installedSection.ToggleCommand.Execute(null);
Shot("addons-collapsed");
installedSection.ToggleCommand.Execute(null);

model.Page = LauncherPage.Patches;
var items = model.Patches.Groups.SelectMany(g => g.Items).ToList();
items.First(i => i.HasDistance).IsExpanded = true;
items.First(i => i.HasColors).IsExpanded = true;
items.First(i => i.HasSpacing).IsExpanded = true;
items.First(i => i.Patch.Id == "NoMailDelay").IsOn = false;
model.Patches.Distance = 75;
Shot("patches-pending");
