// Locates the optional test inputs: an unpatched WowExt.exe (SYNASTRIA_TEST_WOWEXT) and the retired
// SynastriaPatcher.ps1 used as the parity reference (SYNASTRIA_TEST_PATCHER), and PatcherHost.exe from
// tools/build-patcher.ps1. Tests needing them skip without them.

using SynastriaLauncher.Core.Patching;

namespace SynastriaLauncher.Tests;

internal static class TestExe
{
    private static readonly Lazy<byte[]?> Original = new(() =>
        Environment.GetEnvironmentVariable("SYNASTRIA_TEST_WOWEXT") is { } path && File.Exists(path)
            ? File.ReadAllBytes(path)
            : null);

    public static byte[] OriginalBytes()
    {
        Assert.SkipWhen(Original.Value is null, "Set SYNASTRIA_TEST_WOWEXT to an unpatched WowExt.exe");
        return (byte[])Original.Value!.Clone();
    }

    public static string PatcherScript()
    {
        var path = Environment.GetEnvironmentVariable("SYNASTRIA_TEST_PATCHER");
        Assert.SkipWhen(path is null || !File.Exists(path), "Set SYNASTRIA_TEST_PATCHER to SynastriaPatcher.ps1");
        return path!;
    }

    public static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "SynastriaLauncher.slnx")))
        {
            folder = folder.Parent;
        }
        return folder?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }

    // PatcherHost.exe from tools/build-patcher.ps1, which runs the DLL's patch code on an exe file.
    public static string PatcherHost() => NativeTool("PatcherHost.exe");

    // PatcherTests.exe from tools/build-patcher.ps1: the DLL's own unit tests.
    public static string PatcherTests() => NativeTool("PatcherTests.exe");

    private static string NativeTool(string name)
    {
        var path = Path.Combine(RepoRoot(), "artifacts", "patcher", "build", "Release", name);
        Assert.SkipWhen(!File.Exists(path), $"Run tools/build-patcher.ps1 to build {name}");
        return path;
    }

    public static string? OriginalPath() =>
        Environment.GetEnvironmentVariable("SYNASTRIA_TEST_WOWEXT") is { } path && File.Exists(path) ? path : null;

    // An exe file laid out the way the Windows loader maps it: headers, then each section at its address.
    public static byte[] MapImage(byte[] file)
    {
        var layout = PeLayout.Read(file)!;
        var image = new byte[layout.SizeOfImage];
        file.AsSpan(0, (int)layout.SizeOfHeaders).CopyTo(image);
        foreach (var section in layout.Sections)
        {
            var size = (int)Math.Min(section.RawSize, section.VirtualSize);
            file.AsSpan((int)section.RawOffset, size).CopyTo(image.AsSpan((int)section.Rva));
        }
        return image;
    }

    // A temporary game folder holding a copy of the original exe, deleted on dispose.
    public sealed class GameCopy : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("launcher-exe-").FullName;
        public string ExePath => Path.Combine(Folder, "WowExt.exe");

        public GameCopy(byte[] bytes) => File.WriteAllBytes(ExePath, bytes);

        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}
