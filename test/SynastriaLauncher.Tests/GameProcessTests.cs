// Tests for telling this game folder's client apart from other WoW clients running on the PC.
using SynastriaLauncher.Core;

namespace SynastriaLauncher.Tests;

public class GameProcessTests
{
    // A client started from the game folder counts, whatever the letter case or a trailing slash.
    [Fact]
    public void ClientInTheGameFolderCounts()
    {
        Assert.True(GameProcesses.BelongsTo(@"D:\Games\Synastria\WowExt.exe", @"D:\Games\Synastria"));
        Assert.True(GameProcesses.BelongsTo(@"d:\games\synastria\Wow.exe", @"D:\Games\Synastria\"));
    }

    // Retail WoW, another server's client, or a copy in a subfolder doesn't block this folder.
    [Fact]
    public void ClientsElsewhereDontCount()
    {
        Assert.False(GameProcesses.BelongsTo(@"C:\Program Files (x86)\World of Warcraft\_retail_\Wow.exe", @"D:\Games\Synastria"));
        Assert.False(GameProcesses.BelongsTo(@"D:\Games\Synastria Test\WowExt.exe", @"D:\Games\Synastria"));
        Assert.False(GameProcesses.BelongsTo(@"D:\Games\Synastria\Backup\WowExt.exe", @"D:\Games\Synastria"));
    }

    // A running process's exe path is read from Windows (here the test host's own).
    [Fact]
    public void ReadsARunningProcessPath()
    {
        Assert.Equal(Environment.ProcessPath, GameProcesses.ImagePath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        Assert.Null(GameProcesses.ImagePath(-1));
    }

    // A client whose path can't be read is assumed to be this folder's, so nothing is changed under it.
    [Fact]
    public void UnreadablePathCounts()
    {
        Assert.True(GameProcesses.BelongsTo(null, @"D:\Games\Synastria"));
    }
}
