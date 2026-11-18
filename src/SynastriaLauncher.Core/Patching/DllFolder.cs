using System.Text.RegularExpressions;

namespace SynastriaLauncher.Core.Patching;

public static partial class DllFolder
{
    public const string Default = "DLLs";
    public const int Size = 0x40;

    [GeneratedRegex(@"^[A-Za-z0-9_\- ]+(\\[A-Za-z0-9_\- ]+)*$")]
    private static partial Regex FolderPattern();

    // A plain folder inside the game folder (subfolders allowed, no dots), with backslashes; null when invalid.
    public static string? Normalize(string text)
    {
        var folder = text.Trim().Replace('/', '\\').Trim('\\');
        return FolderPattern().IsMatch(folder) && folder.Length < Size ? folder : null;
    }
}
