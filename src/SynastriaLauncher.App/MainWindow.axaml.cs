using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.App.ViewModels;
using SynastriaLauncher.Core.Accounts;

namespace SynastriaLauncher.App;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow() : this(null)
    {
    }

    // From a shortcut (--launch) the window starts minimized and the launcher closes once the game starts; if it
    // can't, the window comes up and the launcher carries on as usual.
    public MainWindow(LaunchRequest? launch)
    {
        InitializeComponent();
        var model = new MainViewModel(this);
        DataContext = model;
        WindowState = launch is null ? WindowState.Normal : WindowState.Minimized;
        Opened += async (_, _) =>
        {
            var launched = launch is not null && await model.QuickLaunchAsync(launch);
            if (launched)
            {
                Close();
            }
            else
            {
                WindowState = WindowState.Normal;
                await model.StartAsync();
            }
        };
    }

    public Task AlertAsync(string title, string message) => ShowDialogAsync(title, message, null, "OK");

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string cancelLabel = "Cancel") =>
        ShowDialogAsync(title, message, confirmLabel, cancelLabel);

    private async Task<bool> ShowDialogAsync(string title, string message, string? confirmLabel, string cancelLabel)
    {
        WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
        };
        var confirmed = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (confirmLabel is not null)
        {
            var confirm = new Button { Content = confirmLabel, Classes = { "primary" } };
            confirm.Click += (_, _) => { confirmed = true; dialog.Close(); };
            buttons.Children.Add(confirm);
        }
        var close = new Button { Content = cancelLabel };
        close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(close);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons,
            },
        };
        await dialog.ShowDialog(this);
        return confirmed;
    }

    public async Task<AccountForm?> EditAccountAsync(string title, AccountForm? existing, Func<string, bool> nameFree)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
        };
        var name = new TextBox { Text = existing?.Name ?? "" };
        var password = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = existing is null ? "" : "Leave empty to keep the saved password",
        };
        var character = new TextBox { Text = existing?.Character ?? "", PlaceholderText = "Optional" };
        var problem = new TextBlock { Classes = { "small" }, Foreground = this.FindResource("Bad") as Avalonia.Media.IBrush, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AccountForm? result = null;
        var save = new Button { Content = "Save", Classes = { "primary" } };
        save.Click += (_, _) =>
        {
            var account = (name.Text ?? "").Trim();
            var secret = password.Text ?? "";
            var hero = (character.Text ?? "").Trim();
            var error = account.Length == 0 ? "Enter the account name you log in with."
                : !nameFree(account) ? $"There's already an account named {account}."
                : existing is null && secret.Length == 0 ? "Enter the account's password."
                : !LoginHandoff.CanCarry(account, secret, hero) ? "The details hold a character the game can't be given."
                : null;
            problem.Text = error ?? "";
            problem.IsVisible = error is not null;
            if (error is null)
            {
                result = new AccountForm(account, secret.Length > 0 ? secret : null, hero.Length > 0 ? hero : null);
                dialog.Close();
            }
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Classes = { "name" }, Text = "Account Name" },
                name,
                new TextBlock { Classes = { "name" }, Text = "Password", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
                password,
                new TextBlock { Classes = { "name" }, Text = "Character", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
                character,
                new TextBlock
                {
                    Classes = { "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Text = "With a character, the game enters the world as them; without one, it stops at character select. "
                        + "If several of your characters share a name, add the class, like Expulsion (Hunter). "
                        + "The password is encrypted for your Windows user.",
                },
                problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { save, cancel },
                },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var start = startFolder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string typeName, string pattern)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [pattern] }],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
