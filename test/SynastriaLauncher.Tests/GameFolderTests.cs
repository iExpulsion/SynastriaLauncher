// Covers GameFolder: recognizing a Synastria game folder and picking the first one from a candidate list.

using SynastriaLauncher.Core;

namespace SynastriaLauncher.Tests;

public sealed class GameFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeFolder(string name, bool withExe)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        if (withExe)
        {
            File.WriteAllBytes(Path.Combine(path, GameFolder.ExeName), []);
        }
        return path;
    }

    // A folder holding WowExt.exe counts as a game folder.
    [Fact]
    public void FolderWithExeIsGameFolder() =>
        Assert.True(GameFolder.IsGameFolder(MakeFolder("game", withExe: true)));

    // A folder without WowExt.exe, a missing folder and an empty path don't.
    [Fact]
    public void OtherPathsAreNotGameFolders()
    {
        Assert.False(GameFolder.IsGameFolder(MakeFolder("empty", withExe: false)));
        Assert.False(GameFolder.IsGameFolder(Path.Combine(_root, "missing")));
        Assert.False(GameFolder.IsGameFolder(null));
        Assert.False(GameFolder.IsGameFolder(""));
    }

    // Find skips nulls and non-game folders and returns the first game folder in order.
    [Fact]
    public void FindReturnsFirstGameFolder()
    {
        var first = MakeFolder("first", withExe: true);
        var second = MakeFolder("second", withExe: true);
        var found = GameFolder.Find([null, MakeFolder("none", withExe: false), first, second]);
        Assert.Equal(first, found);
    }

    // Find returns null when no candidate is a game folder.
    [Fact]
    public void FindReturnsNullWhenNoneMatch() =>
        Assert.Null(GameFolder.Find([null, MakeFolder("none", withExe: false)]));

    // An installed SynastriaLauncher.dll from another launcher build is replaced with this one's, its settings file
    // stays, and a game without the DLL doesn't get one.
    [Fact]
    public void RefreshesThePatcherDllOnlyWhenInstalled()
    {
        var game = MakeFolder("game", withExe: true);
        var exe = new SynastriaLauncher.Core.Patching.GameExe(game);
        byte[] current = [1, 2, 3];
        Assert.False(exe.RefreshPatcherDll(current));
        Assert.False(File.Exists(exe.DllPath));
        File.WriteAllBytes(exe.DllPath, [9, 9]);
        File.WriteAllText(exe.IniPath, "[Patches]\n");
        Assert.True(exe.RefreshPatcherDll(current));
        Assert.Equal(current, File.ReadAllBytes(exe.DllPath));
        Assert.Equal("[Patches]\n", File.ReadAllText(exe.IniPath));
        Assert.False(exe.RefreshPatcherDll(current));
    }
}
