// Saved accounts for auto login: password encryption, the value handed to the game, the launcher's own launch
// arguments, and that the native DLL reads the same variable name and separator.
using SynastriaLauncher.Core;
using SynastriaLauncher.Core.Accounts;

namespace SynastriaLauncher.Tests;

public class AccountTests
{
    // A password survives encryption, and the saved text doesn't contain it.
    [Fact]
    public void PasswordRoundTrips()
    {
        var saved = AccountSecrets.Protect("hunter2 ünï");
        Assert.DoesNotContain("hunter2", saved);
        Assert.Equal("hunter2 ünï", AccountSecrets.Unprotect(saved));
    }

    // Text that isn't an encrypted password for this user reads as unreadable, not as an error.
    [Fact]
    public void DamagedPasswordIsUnreadable()
    {
        Assert.Null(AccountSecrets.Unprotect("not base64!"));
        Assert.Null(AccountSecrets.Unprotect(Convert.ToBase64String([1, 2, 3, 4])));
    }

    // The game gets account, password and character joined by the unit separator, with an empty character allowed.
    [Fact]
    public void HandoffJoinsTheDetails()
    {
        Assert.Equal("Acc\x1Fpw\x1FLich", LoginHandoff.Value("Acc", "pw", "Lich"));
        Assert.Equal("Acc\x1Fpw\x1F", LoginHandoff.Value("Acc", "pw", null));
        Assert.True(LoginHandoff.CanCarry("Acc", "pw", null));
        Assert.False(LoginHandoff.CanCarry("Acc", "p\x1Fw", null));
    }

    // --launch alone uses the Launch button's target; --account and --character name one; no --launch means a normal start.
    [Fact]
    public void LaunchArgumentsParse()
    {
        Assert.Null(LaunchRequest.Parse([]));
        Assert.Null(LaunchRequest.Parse(["--account", "Acc"]));
        Assert.Equal(new LaunchRequest(null), LaunchRequest.Parse(["--launch"]));
        Assert.Equal(new LaunchRequest("My Acc"), LaunchRequest.Parse(["--LAUNCH", "--account", "My Acc"]));
        Assert.Equal(new LaunchRequest(null), LaunchRequest.Parse(["--launch", "--account"]));
        Assert.Equal(new LaunchRequest("Acc", "Lich (Horde)"), LaunchRequest.Parse(["--launch", "--account", "Acc", "--character", "Lich (Horde)"]));
        Assert.Equal("--launch --account \"My Acc\"", LaunchRequest.Arguments("My Acc"));
        Assert.Equal("--launch --account \"Acc\" --character \"Lich (Horde)\"", LaunchRequest.Arguments("Acc", "Lich (Horde)"));
    }

    // The Launch target is the saved account and, when it still has it, the saved character (by name, without case).
    [Fact]
    public void LaunchTargetFollowsSavedAccounts()
    {
        var data = new LauncherData
        {
            Accounts = [new SavedAccount { Name = "Expulsion", Characters = ["Lich (Horde)", "Expulsion (Hunter)"] }],
            LaunchAccount = "expulsion",
            LaunchCharacter = "LICH (HORDE)",
        };
        Assert.Equal(new LaunchTarget("Expulsion", "Lich (Horde)"), data.LaunchTarget);
        data.LaunchCharacter = "Gone";
        Assert.Equal(new LaunchTarget("Expulsion", null), data.LaunchTarget);
        data.LaunchAccount = "Other";
        Assert.Null(data.LaunchTarget);
        Assert.True(data.Accounts[0].HasCharacter(" expulsion (hunter) "));
    }

    // Labels and captions read the way the Launch menu and button show them.
    [Fact]
    public void LaunchTargetLabels()
    {
        Assert.Equal("Expulsion: Character Select", new LaunchTarget("Expulsion", null).Label);
        Assert.Equal("Expulsion: Lich (Horde)", new LaunchTarget("Expulsion", "Lich (Horde)").Label);
        Assert.Equal("as Expulsion", new LaunchTarget("Expulsion", null).Caption);
        Assert.Equal("as Lich (Horde)", new LaunchTarget("Expulsion", "Lich (Horde)").Caption);
    }

    // Accounts are found by name without case; none for a null name.
    [Fact]
    public void AccountsFoundByName()
    {
        var data = new LauncherData { Accounts = [new SavedAccount { Name = "Expulsion" }] };
        Assert.Same(data.Accounts[0], data.FindAccount("EXPULSION"));
        Assert.Null(data.FindAccount("Other"));
        Assert.Null(data.FindAccount(null));
    }

    // The DLL reads the variable name and separator the launcher writes.
    [Fact]
    public void NativeDllAgreesOnTheHandoff()
    {
        var header = File.ReadAllText(Path.Combine(TestExe.RepoRoot(), "native", "SynastriaLauncher", "src", "AutoLogin.h"));
        Assert.Contains($"L\"{LoginHandoff.EnvironmentName}\"", header);
        Assert.Contains(@"L'\x1F'", header);
    }
}
