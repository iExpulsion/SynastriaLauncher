namespace SynastriaLauncher.Core;

// Checks for paths and names that come from a published list, so a list can't point outside the game folder.
public static class SafePaths
{
    public static bool IsRelative(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains(':')
            && parts.All(p => p.Length > 0 && p != "." && p != "..");
    }

    // One folder name, nothing else. Windows drops trailing dots and spaces, so "..." would mean the parent folder
    // and "Foo." would mean "Foo".
    public static bool IsFolderName(string name) =>
        name.Trim().Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.')
        && !name.EndsWith(' ');

    // Removes a temporary folder; a scanner still holding a file mustn't turn a finished install into a failure.
    public static void DeleteTemp(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for Windows' temp cleanup.
        }
    }

    // A folder name that resolves to a folder directly inside parent.
    public static bool IsChildFolder(string parent, string name) =>
        IsFolderName(name) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(parent, name))),
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
