// Covers the launcher's self-update hand-off without the network: downloading beside the exe from file:// URLs,
// the new exe replacing the old one, and cleaning up what an update leaves behind.

using SynastriaLauncher.Core;
using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Tests;

public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("launcher-selfupdate-").FullName;
    private readonly GitHubKeyless _github = new();

    public void Dispose()
    {
        _github.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Exe => Path.Combine(_root, "SynastriaLauncher.exe");

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] FakeExe(byte fill) => [(byte)'M', (byte)'Z', .. Enumerable.Repeat(fill, 4096)];

    private LauncherUpdate UpdateFrom(string path) => new("v9.9.9", new Version(9, 9, 9), new Uri(path).AbsoluteUri);

    // The update downloads beside the exe as .new and leaves the running exe untouched.
    [Fact]
    public async Task DownloadsBesideTheExe()
    {
        File.WriteAllBytes(Exe, FakeExe(1));
        var source = WriteFile("release.exe", FakeExe(2));
        var incoming = await SelfUpdate.DownloadAsync(_github, UpdateFrom(source), Exe, TestContext.Current.CancellationToken);
        Assert.Equal(Exe + ".new", incoming);
        Assert.Equal(FakeExe(2), File.ReadAllBytes(incoming));
        Assert.Equal(FakeExe(1), File.ReadAllBytes(Exe));
    }

    // A download that isn't a program (an error page, say) is refused and removed.
    [Fact]
    public async Task RefusesADownloadThatIsNotAnExe()
    {
        File.WriteAllBytes(Exe, FakeExe(1));
        var source = WriteFile("release.exe", System.Text.Encoding.UTF8.GetBytes(new string('x', 4096)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SelfUpdate.DownloadAsync(_github, UpdateFrom(source), Exe, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Exe + ".new"));
    }

    // The new exe replaces the old one once the old launcher's process is gone, and leaves no temporary copy.
    [Fact]
    public void FinishReplacesTheExe()
    {
        File.WriteAllBytes(Exe, FakeExe(1));
        var incoming = WriteFile("SynastriaLauncher.exe.new", FakeExe(2));
        SelfUpdate.Finish(incoming, Exe, waitForProcessId: int.MaxValue);
        Assert.Equal(FakeExe(2), File.ReadAllBytes(Exe));
        Assert.False(File.Exists(Exe + ".tmp"));
    }

    // While the old exe is still locked, Finish keeps retrying and gives up with the old exe intact.
    [Fact]
    public void FinishLeavesTheOldExeWhenItStaysLocked()
    {
        File.WriteAllBytes(Exe, FakeExe(1));
        var incoming = WriteFile("SynastriaLauncher.exe.new", FakeExe(2));
        using (new FileStream(Exe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => SelfUpdate.Finish(incoming, Exe, int.MaxValue, TimeSpan.FromMilliseconds(600)));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.GetType().Name);
        }
        Assert.Equal(FakeExe(1), File.ReadAllBytes(Exe));
    }

    // The finish arguments carry the exe to replace and the process to wait for.
    [Fact]
    public void FinishArgsNameTheExeAndProcess() =>
        Assert.Equal([SelfUpdate.FinishArg, Exe, "1234"], SelfUpdate.FinishArgs(Exe, 1234));

    // Clean up removes the downloaded copy, a failed temporary copy and an older launcher's .old exe.
    [Fact]
    public void CleanUpRemovesLeftovers()
    {
        File.WriteAllBytes(Exe, FakeExe(1));
        foreach (var suffix in new[] { ".new", ".tmp", ".old" })
        {
            File.WriteAllBytes(Exe + suffix, FakeExe(2));
        }
        SelfUpdate.CleanUp(Exe);
        Assert.Equal(["SynastriaLauncher.exe"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }
}
