using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Services;

/// <summary>Cancel is the default value so a dialog closed from the window chrome never confirms anything.</summary>
public enum DialogChoice
{
    Cancel = 0,
    Primary = 1,
    Secondary = 2,
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "Cancel");

    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    Task<string?> PickSaveFileAsync(string suggestedFileName, string? startDirectory);

    Task<string?> PickFolderAsync(string title, string? startDirectory);

    Task<bool> ShowAccountSetupAsync(AccountSetupViewModel viewModel);

    Task ShowSettingsAsync(SettingsViewModel viewModel);

    void ShowCompose(ComposeViewModel viewModel);
}
