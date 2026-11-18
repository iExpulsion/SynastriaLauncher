namespace SynastriaLauncher.Core;

public static class GameFolder
{
    public const string ExeName = "WowExt.exe";

    public static bool IsGameFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, ExeName));

    public static string? Find(IEnumerable<string?> candidates) =>
        candidates.FirstOrDefault(IsGameFolder);

    public static string AddOnsPath(string gameFolder) => Path.Combine(gameFolder, "Interface", "AddOns");

    // The launcher's own folder, the folder it used last, then the one SynastriaPatcher remembered.
    public static string? Detect(string? saved) => Find([AppContext.BaseDirectory, saved, PatcherSavedPath()]);

    private static string? PatcherSavedPath()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SynastriaPatcher", "GamePath.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }
}
