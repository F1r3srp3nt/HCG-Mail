using HCG.Mail.App.ViewModels;

namespace HCG.Mail.App.Services;

/// <summary>Desktop dialogs and windows; the platform-neutral prompts come from <see cref="IUserPrompts"/>.</summary>
public interface IDialogService : IUserPrompts
{
    Task<bool> ShowAccountSetupAsync(AccountSetupViewModel viewModel);

    Task ShowSettingsAsync(SettingsViewModel viewModel);

    void ShowCompose(ComposeViewModel viewModel);

    void ShowMailHealth(MailHealthViewModel viewModel);

    Task<bool> ShowMicrosoftAccountAsync(MicrosoftAccountViewModel viewModel);

    Task<bool> ShowGoogleAccountAsync(GoogleAccountViewModel viewModel);
}
