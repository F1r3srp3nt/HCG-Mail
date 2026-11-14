using HCG.Mail.App.Preview;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Email;
using HCG.Mail.Email.Deliverability;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Email.Graph;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Logging;
using HCG.Mail.Infrastructure.Storage;
using HCG.Mail.Mobile.Platform;
using HCG.Mail.Mobile.Services;
using HCG.Mail.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Mobile;

/// <summary>
/// Composition root for iPhone and iPad. The mail engine, stores and view models are the shared ones; only the
/// platform (Keychain, Microsoft sign-in, share sheet) and the presentation services (sheets, banners) differ.
/// Preview mode swaps every persistent or networked service for an in-memory one and never contacts a server.
/// </summary>
public static class MobileServices
{
    public const string GoogleNotOnIos =
        "Google sign-in is not available on iPhone and iPad yet. Gmail can be added on the desktop app, or here with IMAP and a Google app password where the account allows app passwords.";

    public static ServiceProvider Build(IMobilePlatform platform, AppMode mode, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        var paths = platform.Paths;

        services.AddSingleton(platform);
        services.AddSingleton(mode);
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (!mode.IsPreview)
            {
                try
                {
                    paths.EnsureCreated();
                    builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Continue without file logging if the app's storage is unavailable.
                }
            }
        });

        if (mode.IsPreview)
        {
            var vault = new SessionCredentialVault();
            vault.SetAsync(PreviewMailSession.SampleAccount.CredentialKey, "preview").GetAwaiter().GetResult();
            var accounts = new InMemoryAccountStore();
            accounts.Save([PreviewMailSession.SampleAccount]);
            services.AddSingleton<ICredentialVault>(vault);
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAppSettingsStore>(new InMemoryAppSettingsStore());
            services.AddSingleton<IMailSessionFactory, PreviewMailSessionFactory>();
            services.AddSingleton<IConnectionTester, PreviewConnectionTester>();
            services.AddSingleton<IDeliveryLog, InMemoryDeliveryLog>();
            services.AddSingleton<IMailDns, OfflineMailDns>();
            services.AddSingleton<IMicrosoftAuth>(new UnconfiguredMicrosoftAuth("Microsoft sign-in is not available in development preview mode."));
            services.AddSingleton<IGoogleAuth>(new UnconfiguredGoogleAuth(GoogleNotOnIos));
        }
        else
        {
            services.AddSingleton(platform.CredentialVault);
            services.AddSingleton<IAccountStore, JsonAccountStore>();
            services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
            services.AddSingleton<IMailSessionFactory, MailSessionFactory>();
            services.AddSingleton(platform.MicrosoftAuth);
            // Google sign-in needs its own iOS OAuth client and redirect; until that exists the shared view models see
            // an unavailable provider, and the iPhone/iPad views offer no Google button.
            services.AddSingleton<IGoogleAuth>(new UnconfiguredGoogleAuth(GoogleNotOnIos));
            services.AddSingleton<IConnectionTester, ConnectionTester>();
            services.AddSingleton<IDeliveryLog, JsonDeliveryLog>();
            services.AddSingleton<IMailDns, SystemMailDns>();
        }

        services.AddSingleton<IMailClientFactory, MailClientFactory>();
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<ITcpProbe, SystemTcpProbe>();
        services.AddSingleton<IHtmlToTextConverter, HtmlToTextConverter>();
        services.AddSingleton<IHtmlSanitizer, MailHtmlSanitizer>();

        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<MobileOverlays>();
        services.AddSingleton<MobileDialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<MobileDialogService>());
        services.AddSingleton<INotificationService, MobileNotificationService>();
        services.AddSingleton<ILinkOpener, MobileLinkOpener>();
        services.AddSingleton<RemoteImageLoader>();
        services.AddSingleton<MailHealthViewModelFactory>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MobileShellViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Mobile defaults that differ from the desktop: attachments always go through the share sheet (there is no
    /// download folder on iOS), and left-over temporary files from the last run are deleted.
    /// </summary>
    public static void PrepareStorage(IMobilePlatform platform, AppSettingsService settings, ILogger logger)
    {
        try
        {
            if (Directory.Exists(platform.TemporaryFilesDirectory))
                Directory.Delete(platform.TemporaryFilesDirectory, recursive: true);
            AppPaths.CreatePrivateDirectory(platform.TemporaryFilesDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clear temporary attachment files ({Error})", ex.GetType().Name);
        }
        if (!settings.Current.AskWhereToSaveAttachments)
            settings.Update(s => s with { AskWhereToSaveAttachments = true });
    }
}
