using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;

namespace HCG.Mail.App.Services;

public sealed class DialogService : IDialogService
{
    private Window? _owner;

    public void Attach(Window owner) => _owner = owner;

    private Window Owner => ActiveWindow() ?? throw new InvalidOperationException("No window is available to host the dialog.");

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var choice = await ShowChoiceAsync(title, message, confirmText, null, "Cancel", destructive);
        return choice == DialogChoice.Primary;
    }

    public Task<DialogChoice> ChooseAsync(string title, string message, string primaryText, string secondaryText, string cancelText = "Cancel") =>
        ShowChoiceAsync(title, message, primaryText, secondaryText, cancelText, false);

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName, string? startDirectory)
    {
        var provider = Owner.StorageProvider;
        var options = new FilePickerSaveOptions
        {
            Title = "Save attachment",
            SuggestedFileName = suggestedFileName,
            ShowOverwritePrompt = true,
        };
        if (startDirectory is not null)
            options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(startDirectory);
        var file = await provider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? startDirectory)
    {
        var provider = Owner.StorageProvider;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startDirectory is not null)
            options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(startDirectory);
        var folders = await provider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ShowAccountSetupAsync(AccountSetupViewModel viewModel)
    {
        var window = new AccountSetupWindow { DataContext = viewModel };
        return await window.ShowDialog<bool>(Owner);
    }

    public Task ShowSettingsAsync(SettingsViewModel viewModel)
    {
        var window = new SettingsWindow { DataContext = viewModel };
        return window.ShowDialog(Owner);
    }

    public void ShowCompose(ComposeViewModel viewModel)
    {
        var window = new ComposeWindow { DataContext = viewModel };
        if (_owner is not null)
            window.Show(_owner);
        else
            window.Show();
    }

    private async Task<DialogChoice> ShowChoiceAsync(string title, string message, string primary, string? secondary, string cancel, bool destructive)
    {
        var dialog = new ConfirmDialog(title, message, primary, secondary, cancel, destructive);
        return await dialog.ShowDialog<DialogChoice>(Owner);
    }

    private Window? ActiveWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var active = desktop.Windows.FirstOrDefault(w => w.IsActive);
            if (active is not null)
                return active;
        }
        return _owner;
    }
}
