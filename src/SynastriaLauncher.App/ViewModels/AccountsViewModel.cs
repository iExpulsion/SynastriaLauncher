using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SynastriaLauncher.App.Services;
using SynastriaLauncher.Core.Accounts;

namespace SynastriaLauncher.App.ViewModels;

// One entry in the Launch choices: a target, or none (Don't Log In).
public sealed record LaunchOption(LaunchTarget? Target)
{
    public string Label => Target?.Label ?? "Don't Log In";
}

// Settings' Accounts section and the Launch button's menu: saved accounts, their characters, what Launch logs in as,
// and shortcuts.
public sealed partial class AccountsViewModel(MainViewModel main) : ObservableObject
{
    public static readonly LaunchOption NoLogin = new((LaunchTarget?)null);

    public ObservableCollection<AccountItemViewModel> Items { get; } = [];
    public ObservableCollection<LaunchOption> LaunchChoices { get; } = [];

    public bool HasAccounts => Items.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchCaption), nameof(HasLaunchCaption))]
    public partial LaunchOption? LaunchChoice { get; set; }

    public string LaunchCaption => LaunchChoice?.Target?.Caption ?? "";
    public bool HasLaunchCaption => LaunchCaption.Length > 0;

    private bool _loading;

    partial void OnLaunchChoiceChanged(LaunchOption? value)
    {
        if (!_loading && value is not null)
        {
            main.Data.LaunchAccount = value.Target?.Account;
            main.Data.LaunchCharacter = value.Target?.Character;
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
            LaunchChoices.Add(new LaunchOption(new LaunchTarget(account.Name, null)));
            foreach (var character in account.Characters)
            {
                LaunchChoices.Add(new LaunchOption(new LaunchTarget(account.Name, character)));
            }
        }
        LaunchChoice = LaunchChoices.FirstOrDefault(o => o.Target == main.Data.LaunchTarget) ?? NoLogin;
        _loading = false;
        OnPropertyChanged(nameof(HasAccounts));
    }

    private void SaveAndLoad()
    {
        main.Data.Save();
        Load();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var form = await main.Dialogs.EditAccountAsync("Add Account", null, name => main.Data.FindAccount(name) is null);
        if (form is { Password: { } password })
        {
            main.Data.Accounts.Add(new SavedAccount { Name = form.Name, Password = AccountSecrets.Protect(password) });
            main.Data.LaunchAccount ??= main.Data.Accounts.Count == 1 ? form.Name : null;
            SaveAndLoad();
        }
    }

    public async Task EditAsync(SavedAccount account)
    {
        var form = await main.Dialogs.EditAccountAsync("Edit Account", new AccountForm(account.Name, null),
            name => main.Data.FindAccount(name) is var found && (found is null || ReferenceEquals(found, account)));
        if (form is not null)
        {
            var wasLaunch = string.Equals(main.Data.LaunchAccount, account.Name, StringComparison.OrdinalIgnoreCase);
            account.Name = form.Name;
            account.Password = form.Password is { } password ? AccountSecrets.Protect(password) : account.Password;
            main.Data.LaunchAccount = wasLaunch ? form.Name : main.Data.LaunchAccount;
            SaveAndLoad();
        }
    }

    public async Task RemoveAsync(SavedAccount account)
    {
        var remove = await main.Dialogs.ConfirmAsync("Remove Account?",
            $"The launcher forgets {account.Name}, its password and its characters. Shortcuts made for it stop logging in.", "Remove");
        if (remove)
        {
            main.Data.Accounts.Remove(account);
            var wasLaunch = string.Equals(main.Data.LaunchAccount, account.Name, StringComparison.OrdinalIgnoreCase);
            main.Data.LaunchAccount = wasLaunch ? null : main.Data.LaunchAccount;
            main.Data.LaunchCharacter = wasLaunch ? null : main.Data.LaunchCharacter;
            SaveAndLoad();
        }
    }

    public async Task AddCharacterAsync(SavedAccount account)
    {
        var character = await main.Dialogs.PromptAsync($"Add a Character to {account.Name}", "Character",
            "The character's name. If several of your characters share it, add their class, race, faction or level, like "
                + "Expulsion (Hunter) or Lich (Horde).",
            text => text.Length == 0 ? "Enter the character's name."
                : account.HasCharacter(text) ? $"{account.Name} already has {text}."
                : !LoginHandoff.CanCarry(account.Name, "", text) ? "The name holds a character the game can't be given."
                : null);
        if (character is not null)
        {
            account.Characters.Add(character);
            SaveAndLoad();
        }
    }

    public void RemoveCharacter(SavedAccount account, string character)
    {
        account.Characters.RemoveAll(c => SavedAccount.SameCharacter(c, character));
        var wasLaunch = main.Data.LaunchTarget is { } target && target.Account == account.Name
            && main.Data.LaunchCharacter is { } launch && SavedAccount.SameCharacter(launch, character);
        main.Data.LaunchCharacter = wasLaunch ? null : main.Data.LaunchCharacter;
        SaveAndLoad();
    }

    public async Task CreateShortcutAsync(SavedAccount account, string? character)
    {
        var target = new LaunchTarget(account.Name, character);
        string? made = null;
        string? problem = null;
        try
        {
            made = Shortcuts.CreateOnDesktop(character is null ? $"Synastria ({account.Name})" : $"Synastria ({character})",
                Environment.ProcessPath!, LaunchRequest.Arguments(account.Name, character),
                character is null ? $"Starts Synastria and logs in as {account.Name}" : $"Starts Synastria and enters the world as {character}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            problem = e.Message;
        }
        await main.Dialogs.AlertAsync(made is not null ? "Shortcut Created" : "Couldn't Create the Shortcut",
            made is not null
                ? $"\"{Path.GetFileNameWithoutExtension(made)}\" is on your desktop. It starts the game through the launcher and logs in {target.Caption}. Keep the launcher where it is, or make the shortcut again after moving it."
                : $"Windows refused it: {problem}");
    }
}

public sealed partial class AccountItemViewModel : ObservableObject
{
    private readonly SavedAccount _account;
    private readonly AccountsViewModel _owner;

    public AccountItemViewModel(SavedAccount account, AccountsViewModel owner)
    {
        _account = account;
        _owner = owner;
        Characters = new(account.Characters.Select(c => new CharacterItemViewModel(account, c, owner)));
    }

    public string Name => _account.Name;
    public ObservableCollection<CharacterItemViewModel> Characters { get; }

    public string Detail => Characters.Count == 0
        ? "Stops at character select. Add a character to go straight into the world."
        : "Its own shortcut stops at character select; each character's goes into the world.";

    public bool PasswordUnreadable => AccountSecrets.Unprotect(_account.Password) is null;

    [RelayCommand]
    private Task EditAsync() => _owner.EditAsync(_account);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(_account);

    [RelayCommand]
    private Task CreateShortcutAsync() => _owner.CreateShortcutAsync(_account, null);

    [RelayCommand]
    private Task AddCharacterAsync() => _owner.AddCharacterAsync(_account);
}

public sealed partial class CharacterItemViewModel(SavedAccount account, string character, AccountsViewModel owner) : ObservableObject
{
    public string Name => character;

    [RelayCommand]
    private Task CreateShortcutAsync() => owner.CreateShortcutAsync(account, character);

    [RelayCommand]
    private void Remove() => owner.RemoveCharacter(account, character);
}
