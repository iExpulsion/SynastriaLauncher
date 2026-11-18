using System.Security.Cryptography;

namespace SynastriaLauncher.Core.Mods;

// Files of the Synastria mods, shipped inside the launcher (Mods/Bundled/<mod id>/<file>), so they install offline
// and update with the launcher.
public static class BundledFiles
{
    private const string Prefix = "SynastriaLauncher.Core.Mods.Bundled/";

    public static byte[]? Read(string name)
    {
        var assembly = typeof(BundledFiles).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(r => r.StartsWith(Prefix, StringComparison.Ordinal) &&
                string.Equals(r[Prefix.Length..].Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
        using var stream = resource is null ? null : assembly.GetManifestResourceStream(resource);
        using var memory = new MemoryStream();
        stream?.CopyTo(memory);
        return stream is null ? null : memory.ToArray();
    }

    public static string? Sha256(string name) =>
        Read(name) is { } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null;
}
