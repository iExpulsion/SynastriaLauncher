using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.App.ViewModels;

namespace SynastriaLauncher.App;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        var model = new MainViewModel(this);
        DataContext = model;
        Opened += async (_, _) => await model.StartAsync();
    }

    public Task AlertAsync(string title, string message) => ShowDialogAsync(title, message, null, "OK");

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string cancelLabel = "Cancel") =>
        ShowDialogAsync(title, message, confirmLabel, cancelLabel);

    private async Task<bool> ShowDialogAsync(string title, string message, string? confirmLabel, string cancelLabel)
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
