using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.App.ViewModels;
using SynastriaLauncher.Core.Accounts;
using SynastriaLauncher.Core.Addons;

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
        BuildLaunchMenu(model);
        model.Accounts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AccountsViewModel.LaunchChoice) or nameof(AccountsViewModel.HasAccounts))
            {
                BuildLaunchMenu(model);
            }
        };
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

    // The Launch button's account menu: with saved accounts, each account and character and Don't Log In (the current one checked);
    // always Add Account and Manage Accounts. Rebuilt whenever the accounts or the choice change, since items added
    // while the menu opens don't show.
    private void BuildLaunchMenu(MainViewModel model)
    {
        var menu = (MenuFlyout)LaunchMenuButton.Flyout!;
        menu.Items.Clear();
        var choices = model.Accounts.LaunchChoices.Where(c => c.Target is not null).Append(AccountsViewModel.NoLogin);
        foreach (var choice in model.Accounts.HasAccounts ? choices : [])
        {
            var item = new MenuItem { Header = choice.Label, ToggleType = MenuItemToggleType.Radio, IsChecked = choice == model.Accounts.LaunchChoice };
            item.Click += (_, _) => model.Accounts.LaunchChoice = choice;
            menu.Items.Add(item);
        }
        if (model.Accounts.HasAccounts)
        {
            menu.Items.Add(new Separator());
        }
        var add = new MenuItem { Header = "Add Account" };
        add.Click += (_, _) => model.Accounts.AddCommand.Execute(null);
        menu.Items.Add(add);
        var manage = new MenuItem { Header = "Manage Accounts" };
        manage.Click += (_, _) => model.Page = LauncherPage.Settings;
        menu.Items.Add(manage);
    }

    public Task AlertAsync(string title, string message) => ShowDialogAsync(title, message, null, "OK");

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string cancelLabel = "Cancel") =>
        ShowDialogAsync(title, message, confirmLabel, cancelLabel);

    public async Task<bool?> ConfirmWithOptionAsync(string title, string message, string option, string confirmLabel)
    {
        var box = new CheckBox { Content = option };
        var confirmed = await ShowDialogAsync(title, message, confirmLabel, "Cancel", box);
        return confirmed ? box.IsChecked == true : null;
    }

    private async Task<bool> ShowDialogAsync(string title, string message, string? confirmLabel, string cancelLabel, Control? extra = null)
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
        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } },
        };
        if (extra is not null)
        {
            body.Children.Add(extra);
        }
        body.Children.Add(buttons);
        dialog.Content = body;
        await dialog.ShowDialog(this);
        return confirmed;
    }

    public async Task<AccountForm?> EditAccountAsync(string title, AccountForm? existing, Func<string, bool> nameFree)
    {
        var name = new TextBox { Text = existing?.Name ?? "" };
        var password = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = existing is null ? "" : "Leave empty to keep the saved password",
        };
        var problem = ProblemText();
        AccountForm? result = null;
        var dialog = FormWindow(title, out var save, out var body);
        save.Click += (_, _) =>
        {
            var account = (name.Text ?? "").Trim();
            var secret = password.Text ?? "";
            var error = account.Length == 0 ? "Enter the account name you log in with."
                : !nameFree(account) ? $"There's already an account named {account}."
                : existing is null && secret.Length == 0 ? "Enter the account's password."
                : !LoginHandoff.CanCarry(account, secret, null) ? "The details hold a character the game can't be given."
                : null;
            problem.Text = error ?? "";
            problem.IsVisible = error is not null;
            if (error is null)
            {
                result = new AccountForm(account, secret.Length > 0 ? secret : null);
                dialog.Close();
            }
        };
        body.Children.InsertRange(0,
        [
            new TextBlock { Classes = { "name" }, Text = "Account Name" },
            name,
            new TextBlock { Classes = { "name" }, Text = "Password", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            password,
            new TextBlock
            {
                Classes = { "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = "The password is encrypted for your Windows user. Add characters to the account afterwards to go straight into the world.",
            },
            problem,
        ]);
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<string?> PromptAsync(string title, string label, string help, Func<string, string?> check)
    {
        var text = new TextBox();
        var problem = ProblemText();
        string? result = null;
        var dialog = FormWindow(title, out var save, out var body);
        save.Click += (_, _) =>
        {
            var value = (text.Text ?? "").Trim();
            var error = check(value);
            problem.Text = error ?? "";
            problem.IsVisible = error is not null;
            if (error is null)
            {
                result = value;
                dialog.Close();
            }
        };
        body.Children.InsertRange(0,
        [
            new TextBlock { Classes = { "name" }, Text = label },
            text,
            new TextBlock { Classes = { "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = help },
            problem,
        ]);
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<CharacterCopyForm?> CopyCharacterSettingsAsync(IReadOnlyList<WtfCharacter> characters, WtfCharacter? from)
    {
        var labels = characters.Select(c => c.Label).ToList();
        var fromIndex = from is null ? 0 : Math.Max(0, characters.ToList().FindIndex(c => c.SameAs(from)));
        var source = new ComboBox { ItemsSource = labels, SelectedIndex = fromIndex, HorizontalAlignment = HorizontalAlignment.Stretch };
        var target = new ComboBox { ItemsSource = labels, SelectedIndex = fromIndex == 0 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        (CheckBox Box, CharacterParts Part)[] parts =
        [
            (new CheckBox { Content = "Addon Profiles", IsChecked = true }, CharacterParts.AddonProfiles),
            (new CheckBox { Content = "Enabled Addons", IsChecked = true }, CharacterParts.EnabledAddons),
            (new CheckBox { Content = "Frame Positions and Chat Windows", IsChecked = true }, CharacterParts.Layout),
            (new CheckBox { Content = "Game Options", IsChecked = true }, CharacterParts.GameOptions),
        ];
        var problem = ProblemText();
        CharacterCopyForm? result = null;
        var dialog = FormWindow("Copy Character Settings", out var copy, out var body);
        copy.Content = "Copy";
        copy.Click += (_, _) =>
        {
            var chosen = parts.Where(p => p.Box.IsChecked == true).Aggregate(CharacterParts.None, (all, p) => all | p.Part);
            var error = source.SelectedIndex == target.SelectedIndex ? "Pick two different characters."
                : chosen == CharacterParts.None ? "Pick at least one thing to copy."
                : null;
            problem.Text = error ?? "";
            problem.IsVisible = error is not null;
            if (error is null)
            {
                result = new CharacterCopyForm(characters[source.SelectedIndex], characters[target.SelectedIndex], chosen);
                dialog.Close();
            }
        };
        body.Children.InsertRange(0,
        [
            new TextBlock { Classes = { "name" }, Text = "Copy From" },
            source,
            new TextBlock { Classes = { "name" }, Text = "Copy To", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            target,
            new StackPanel { Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { parts[0].Box, parts[1].Box, parts[2].Box, parts[3].Box } },
            new TextBlock
            {
                Classes = { "small" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = "Addon profiles are linked, not copied: addons that use profiles (ElvUI, Skada and others) give both characters "
                    + "the same one, so later changes show up on both. That works only on the same account. Game options leave "
                    + "out tracked quests and other character state. Everything replaced is backed up first.",
            },
            problem,
        ]);
        await dialog.ShowDialog(this);
        return result;
    }

    private TextBlock ProblemText() => new()
    {
        Classes = { "small" }, Foreground = this.FindResource("Bad") as Avalonia.Media.IBrush, IsVisible = false,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    // A small dialog window: its body ends with Save and Cancel; the caller puts its fields in front.
    private Window FormWindow(string title, out Button save, out StackPanel body)
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
        save = new Button { Content = "Save", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close();
        body = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { save, cancel },
                },
            },
        };
        dialog.Content = body;
        return dialog;
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
