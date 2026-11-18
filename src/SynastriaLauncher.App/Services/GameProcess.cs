using System.ComponentModel;
using System.Diagnostics;

namespace SynastriaLauncher.App.Services;

public static class GameProcess
{
    public static bool IsRunning() =>
        Process.GetProcessesByName("WowExt").Concat(Process.GetProcessesByName("Wow")).Any();

    public static void Start(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath)!, UseShellExecute = true });

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
