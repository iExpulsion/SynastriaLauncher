using Avalonia;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core;

namespace SynastriaLauncher.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is [SelfUpdate.FinishArg, var exePath, var processId] && int.TryParse(processId, out var id))
        {
            FinishUpdate(exePath, id);
        }
        else
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    // Started from the downloaded update: put it in place of the old launcher, then start that. On failure the old
    // launcher starts as it was, and the details go to the crash log.
    private static void FinishUpdate(string exePath, int processId)
    {
        try
        {
            SelfUpdate.Finish(Environment.ProcessPath!, exePath, processId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            App.LogCrash(e);
        }
        GameProcess.Start(exePath);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
