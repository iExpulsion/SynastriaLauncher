using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core.Accounts;

namespace SynastriaLauncher.App.ViewModels;

// Settings' Accounts section: the saved game accounts, the one the Launch button logs in with, and shortcuts.
public sealed partial class AccountsViewModel(MainViewModel main) : ObservableObject
{
    public const string NoLogin = "Don't Log In";

    public ObservableCollection<AccountItemViewModel> Items { get; } = [];
    public ObservableCollection<string> LaunchChoices { get; } = [];

    public bool HasAccounts => Items.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchCaption), nameof(HasLaunchCaption))]
    public partial string? LaunchChoice { get; set; }

    public string LaunchCaption => LaunchChoice is null or NoLogin ? "" : $"as {LaunchChoice}";
    public bool HasLaunchCaption => LaunchCaption.Length > 0;

    // The Launch button gives up room on its right for the account chevron once there are accounts.
    public double LaunchWidth => HasAccounts ? 161 : 200;
    public Avalonia.CornerRadius LaunchCorners => HasAccounts ? new(9, 0, 0, 9) : new(9);

    private bool _loading;

    partial void OnLaunchChoiceChanged(string? value)
    {
        if (!_loading && value is not null)
        {
            main.Data.LaunchAccount = value == NoLogin ? null : value;
            main.Data.Save();
        }
    }

    public void Load()
    {
        _loading = true;
        Items.Clear();
        LaunchChoices.Clear();
        LaunchChoices.Add(NoLogin);
        foreach (var account in main.Data.Accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new AccountItemViewModel(account, this));
            LaunchChoices.Add(account.Name);
        }
        LaunchChoice = main.Data.FindAccount(main.Data.LaunchAccount)?.Name ?? NoLogin;
        _loading = false;
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(LaunchWidth));
        OnPropertyChanged(nameof(LaunchCorners));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var form = await main.Dialogs.EditAccountAsync("Add Account", null, name => main.Data.FindAccount(name) is null);
        if (form is { Password: { } password })
        {
            main.Data.Accounts.Add(new SavedAccount { Name = form.Name, Character = form.Character, Password = AccountSecrets.Protect(password) });
            main.Data.LaunchAccount ??= main.Data.Accounts.Count == 1 ? form.Name : null;
            main.Data.Save();
            Load();
        }
    }

    public async Task EditAsync(SavedAccount account)
    {
        var form = await main.Dialogs.EditAccountAsync("Edit Account", new AccountForm(account.Name, null, account.Character),
            name => main.Data.FindAccount(name) is var found && (found is null || ReferenceEquals(found, account)));
        if (form is not null)
        {
            var wasLaunch = string.Equals(main.Data.LaunchAccount, account.Name, StringComparison.OrdinalIgnoreCase);
            account.Name = form.Name;
            account.Character = form.Character;
            account.Password = form.Password is { } password ? AccountSecrets.Protect(password) : account.Password;
            main.Data.LaunchAccount = wasLaunch ? form.Name : main.Data.LaunchAccount;
            main.Data.Save();
            Load();
        }
    }

    public async Task RemoveAsync(SavedAccount account)
    {
        var remove = await main.Dialogs.ConfirmAsync("Remove Account?",
            $"The launcher forgets {account.Name} and its password. Shortcuts made for it stop logging in.", "Remove");
        if (remove)
        {
            main.Data.Accounts.Remove(account);
            main.Data.LaunchAccount = string.Equals(main.Data.LaunchAccount, account.Name, StringComparison.OrdinalIgnoreCase)
                ? null : main.Data.LaunchAccount;
            main.Data.Save();
            Load();
        }
    }

    public async Task CreateShortcutAsync(SavedAccount account)
    {
        string? made = null;
        string? problem = null;
        try
        {
            made = Shortcuts.CreateOnDesktop($"Synastria ({account.Name})", Environment.ProcessPath!,
                LaunchRequest.Arguments(account.Name), $"Starts Synastria and logs in as {account.Name}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            problem = e.Message;
        }
        await main.Dialogs.AlertAsync(made is not null ? "Shortcut Created" : "Couldn't Create the Shortcut",
            made is not null
                ? $"\"{Path.GetFileNameWithoutExtension(made)}\" is on your desktop. It starts the game through the launcher and logs in as {account.Name}. Keep the launcher where it is, or make the shortcut again after moving it."
                : $"Windows refused it: {problem}");
    }
}

public sealed partial class AccountItemViewModel(SavedAccount account, AccountsViewModel owner) : ObservableObject
{
    public string Name => account.Name;

    public string Detail => string.IsNullOrWhiteSpace(account.Character)
        ? "Logs in and stops at character select"
        : $"Logs in and enters the world as {account.Character}";

    public bool PasswordUnreadable => AccountSecrets.Unprotect(account.Password) is null;

    [RelayCommand]
    private Task EditAsync() => owner.EditAsync(account);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(account);

    [RelayCommand]
    private Task CreateShortcutAsync() => owner.CreateShortcutAsync(account);
}
