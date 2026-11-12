namespace HCG.Mail.App.Services;

/// <summary>Cancel is the default value so a dialog closed from the window chrome never confirms anything.</summary>
public enum DialogChoice
{
    Cancel = 0,
    Primary = 1,
    Secondary = 2,
}

/// <summary>
/// Questions and file pickers the shared view models need, implemented per platform (desktop dialogs, iOS sheets).
/// File pickers return local file paths the app may read.
/// </summary>
public interface IUserPrompts
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "Cancel");

    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    Task<string?> PickSaveFileAsync(string suggestedFileName, string? startDirectory);

    Task<string?> PickFolderAsync(string title, string? startDirectory);
}
