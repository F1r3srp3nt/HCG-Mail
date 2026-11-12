using Avalonia.Headless.XUnit;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Settings;
using HCG.Mail.Mobile.Services;
using HCG.Mail.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Mobile.Tests;

/// <summary>The composition root and mobile services, without a UI.</summary>
public sealed class PlatformServiceTests
{
    [Fact]
    public void Real_mode_uses_the_platform_vault_and_Microsoft_sign_in_never_a_file()
    {
        using var platform = new FakeMobilePlatform();
        using var services = MobileServices.Build(platform, new AppMode(IsPreview: false));
        Assert.Same(platform.CredentialVault, services.GetRequiredService<ICredentialVault>());
        Assert.Same(platform.MicrosoftAuth, services.GetRequiredService<IMicrosoftAuth>());
        Assert.IsType<MobileDialogService>(services.GetRequiredService<IDialogService>());
        Assert.IsType<MobileNotificationService>(services.GetRequiredService<INotificationService>());
        Assert.IsType<MobileLinkOpener>(services.GetRequiredService<ILinkOpener>());
    }

    [Fact]
    public void Storage_is_prepared_for_the_share_sheet_and_leftover_temporary_files_are_removed()
    {
        using var platform = new FakeMobilePlatform();
        Directory.CreateDirectory(Path.Combine(platform.TemporaryFilesDirectory, "saved"));
        File.WriteAllText(Path.Combine(platform.TemporaryFilesDirectory, "saved", "old.pdf"), "x");
        var settings = new AppSettingsService(new HCG.Mail.Infrastructure.Storage.InMemoryAppSettingsStore());
        MobileServices.PrepareStorage(platform, settings, NullLogger.Instance);
        Assert.True(Directory.Exists(platform.TemporaryFilesDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(platform.TemporaryFilesDirectory));
        Assert.True(settings.Current.AskWhereToSaveAttachments); // routes saves through PickSaveFileAsync -> share sheet
    }

    [Fact]
    public async Task Save_paths_stay_inside_the_temporary_folder_even_for_hostile_names()
    {
        using var platform = new FakeMobilePlatform();
        var dialogs = new MobileDialogService(new MobileOverlays(), platform);
        var path = await dialogs.PickSaveFileAsync("../../../etc/passwd", null);
        Assert.NotNull(path);
        Assert.StartsWith(Path.GetFullPath(platform.TemporaryFilesDirectory), Path.GetFullPath(path!));
        Assert.Null(await dialogs.PickFolderAsync("x", null));
        Assert.Empty(await dialogs.PickFilesAsync("x")); // no window attached: nothing picked, no exception
    }

    [Fact]
    public async Task Prompts_queue_and_answer_in_order()
    {
        var overlays = new MobileOverlays();
        var first = overlays.AskAsync(new PromptViewModel("A", "a", "OK", null, "Cancel", false));
        var second = overlays.AskAsync(new PromptViewModel("B", "b", "OK", null, "Cancel", true));
        Assert.Equal("A", overlays.Prompt!.Title);
        overlays.Prompt.PrimaryCommand.Execute(null);
        Assert.Equal(DialogChoice.Primary, await first);
        Assert.Equal("B", overlays.Prompt!.Title);
        Assert.True(overlays.Prompt.IsDestructive);
        overlays.Prompt.CancelCommand.Execute(null);
        Assert.Equal(DialogChoice.Cancel, await second);
        Assert.Null(overlays.Prompt);
    }

    [Theory]
    [InlineData(0.8, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.35, 1.35)]
    [InlineData(3.1, MobileShellViewModel.MaxTextScale)]
    [InlineData(double.NaN, 1.0)]
    public void Text_scale_is_clamped(double input, double expected) =>
        Assert.Equal(expected, MobileShellViewModel.ClampTextScale(input));

    [Fact]
    public void Errors_stay_on_screen_longer_than_confirmations() =>
        Assert.True(MobileOverlays.BannerDuration(BannerKind.Error) > MobileOverlays.BannerDuration(BannerKind.Success));
}
