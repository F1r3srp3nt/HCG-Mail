using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Mail;
using HCG.Mail.Mobile.Platform;
using HCG.Mail.Mobile.ViewModels;

namespace HCG.Mail.Mobile.Services;

/// <summary>
/// The shared dialogs as mobile sheets and in-app alerts. Files picked for sending are copied into the app's temporary
/// folder first, because the iOS document picker only lends access to the original for a moment.
/// </summary>
public sealed class MobileDialogService(MobileOverlays overlays, IMobilePlatform platform) : IDialogService
{
    private Func<TopLevel?> _topLevel = () => null;

    public void AttachTopLevel(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) =>
        await overlays.AskAsync(new PromptViewModel(title, message, confirmText, null, "Cancel", destructive)) == DialogChoice.Primary;

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "Cancel") =>
        overlays.AskAsync(new PromptViewModel(title, message, primaryText, secondaryText, cancelText, false));

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var storage = _topLevel()?.StorageProvider;
        if (storage is null || !storage.CanOpen)
            return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        var copies = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var directory = Path.Combine(platform.TemporaryFilesDirectory, "outgoing", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var target = AttachmentFileNames.GetUniquePath(directory, AttachmentFileNames.Sanitize(file.Name));
                await using (var source = await file.OpenReadAsync())
                await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    await source.CopyToAsync(destination);
                copies.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The ComposeViewModel reports files it cannot attach; an unreadable pick is simply skipped.
            }
            finally
            {
                file.Dispose();
            }
        }
        return copies;
    }

    /// <summary>Attachments are written to the app's temporary folder and then offered in the share sheet.</summary>
    public Task<string?> PickSaveFileAsync(string suggestedFileName, string? startDirectory)
    {
        var directory = Path.Combine(platform.TemporaryFilesDirectory, "saved");
        Directory.CreateDirectory(directory);
        return Task.FromResult<string?>(AttachmentFileNames.GetUniquePath(directory, AttachmentFileNames.Sanitize(suggestedFileName)));
    }

    /// <summary>There is no user-visible download folder on iOS; the share sheet is used instead.</summary>
    public Task<string?> PickFolderAsync(string title, string? startDirectory) => Task.FromResult<string?>(null);

    public async Task<bool> ShowAccountSetupAsync(AccountSetupViewModel viewModel)
    {
        var saved = false;
        var entry = overlays.PushSheet(viewModel);
        viewModel.CloseRequested += (_, result) =>
        {
            saved = result;
            overlays.CloseSheet(viewModel);
        };
        await entry.Closed;
        return saved;
    }

    public Task ShowSettingsAsync(SettingsViewModel viewModel) => overlays.PushSheet(viewModel).Closed;

    public void ShowCompose(ComposeViewModel viewModel)
    {
        overlays.PushSheet(viewModel);
        viewModel.CloseRequested += (_, _) => overlays.CloseSheet(viewModel);
    }

    public void ShowMailHealth(MailHealthViewModel viewModel) => overlays.PushSheet(viewModel);

    public async Task<bool> ShowMicrosoftAccountAsync(MicrosoftAccountViewModel viewModel)
    {
        var connected = false;
        var entry = overlays.PushSheet(viewModel);
        viewModel.CloseRequested += (_, result) =>
        {
            connected = result;
            overlays.CloseSheet(viewModel);
        };
        await entry.Closed;
        return connected;
    }
}
