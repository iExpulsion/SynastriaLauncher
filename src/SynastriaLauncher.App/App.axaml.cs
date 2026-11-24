using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SynastriaLauncher.Core;

namespace SynastriaLauncher.App;

public partial class App : Application
{
    public static string CrashLogPath => Path.Combine(LauncherData.DefaultFolder, "crash.log");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Core.Accounts.LaunchRequest.Parse(desktop.Args ?? []));
        }
        base.OnFrameworkInitializationCompleted();
    }

    // A bug in one action shouldn't close the launcher: log it, say so, and keep running.
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            _ = window.AlertAsync("Something Went Wrong",
                $"{e.Exception.Message}\n\nThe details were saved to {CrashLogPath}. The launcher keeps running, but reopen it if anything looks off.");
        }
    }

    public static void LogCrash(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LauncherData.DefaultFolder);
            File.AppendAllText(CrashLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {SelfUpdate.CurrentVersion}\n{exception}\n\n");
        }
        catch (IOException)
        {
            // Nowhere to write it; the error dialog still shows.
        }
    }
}
