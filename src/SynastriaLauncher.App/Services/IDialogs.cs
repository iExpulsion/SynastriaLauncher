namespace SynastriaLauncher.App.Services;

public interface IDialogs
{
    Task AlertAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string cancelLabel = "Cancel");
    Task<string?> PickFolderAsync(string title, string? startFolder);
    Task<string?> PickFileAsync(string title, string typeName, string pattern);
}
