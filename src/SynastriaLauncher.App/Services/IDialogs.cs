namespace SynastriaLauncher.App.Services;

// An account as the editor shows it. Password is null when editing leaves the saved one as it is.
public sealed record AccountForm(string Name, string? Password);

public interface IDialogs
{
    Task AlertAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string cancelLabel = "Cancel");
    Task<string?> PickFolderAsync(string title, string? startFolder);
    Task<string?> PickFileAsync(string title, string typeName, string pattern);

    // Null when cancelled. A new account (existing null) needs a password; nameFree says whether a name is unused.
    Task<AccountForm?> EditAccountAsync(string title, AccountForm? existing, Func<string, bool> nameFree);

    // One line of text, trimmed; null when cancelled. problem returns why the text can't be used, or null.
    Task<string?> PromptAsync(string title, string label, string help, Func<string, string?> problem);
}
