using System.ComponentModel;
using System.Diagnostics;
using SynastriaLauncher.Core.Accounts;

namespace SynastriaLauncher.App.Services;

public static class GameProcess
{
    public static bool IsRunning(string? gameFolder) => SynastriaLauncher.Core.GameProcesses.IsRunning(gameFolder);

    public static void Start(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath)!, UseShellExecute = true });

    // The game, with the login for SynastriaLauncher.dll in its environment when there is one. That needs the
    // process started directly; without one it starts through the shell as before.
    public static void StartGame(string exePath, string? login)
    {
        var info = new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath)!, UseShellExecute = login is null };
        if (login is not null)
        {
            info.Environment[LoginHandoff.EnvironmentName] = login;
        }
        Process.Start(info)?.Dispose();
    }

    public static void Start(string exePath, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath)!, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        Process.Start(info)?.Dispose();
    }

    // Starts the launcher again with administrator rights (Windows asks first) and reports whether it did.
    public static bool RestartAsAdmin()
    {
        var started = false;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            started = true;
        }
        catch (Win32Exception)
        {
            started = false;
        }
        return started;
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
