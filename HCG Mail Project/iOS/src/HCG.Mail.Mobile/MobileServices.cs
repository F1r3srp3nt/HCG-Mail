using HCG.Mail.Infrastructure.Network;
using HCG.Mail.Email.Offline;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Network;
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
                    StartupTrace.Mark(StartupStage.LocalStorage, "data folder ready");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Continue without file logging if the app's storage is unavailable.
                    StartupTrace.Failure("creating the data folder (continuing without the file log)", ex);
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
            services.AddSingleton<PreviewMailSessionFactory>();
            services.AddSingleton<INetworkMonitor, ManualNetworkMonitor>();
            services.AddSingleton<IMailCache, NoMailCache>();
            services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();
            services.AddSingleton<IMailSessionFactory>(sp => new OfflineCapableSessionFactory(sp.GetRequiredService<PreviewMailSessionFactory>(),
                sp.GetRequiredService<IMailCache>(), sp.GetRequiredService<INetworkMonitor>(), sp.GetRequiredService<ILoggerFactory>()));
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
            services.AddSingleton<MailSessionFactory>();
            services.AddSingleton<INetworkMonitor, SystemNetworkMonitor>();
            services.AddSingleton<IMailCache>(sp => new JsonMailCache(sp.GetRequiredService<AppPaths>(),
                () => sp.GetRequiredService<AppSettingsService>().Current.KeepOfflineCopies, sp.GetRequiredService<ILogger<JsonMailCache>>()));
            services.AddSingleton<IOutboxStore, JsonOutboxStore>();
            // Every session keeps mail readable offline and fails fast while the server cannot be reached.
            services.AddSingleton<IMailSessionFactory>(sp => new OfflineCapableSessionFactory(sp.GetRequiredService<MailSessionFactory>(),
                sp.GetRequiredService<IMailCache>(), sp.GetRequiredService<INetworkMonitor>(), sp.GetRequiredService<ILoggerFactory>()));
            // A build without the Microsoft capability never activates Microsoft sign-in; existing accounts stay as they are.
            services.AddSingleton(platform.Capabilities.MicrosoftSignIn
                ? platform.MicrosoftAuth
                : new UnconfiguredMicrosoftAuth(HCG.Mail.Core.Release.ReleaseCapabilities.MicrosoftUnavailableMessage));
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
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INetworkInterfaces, SystemNetworkInterfaces>();
        services.AddSingleton<IHttpProbe, SystemHttpProbe>();
        services.AddSingleton<NetworkDoctor>();
        // Optional: a host that ships HCG VPN registers its own IHcgVpn. HCG Mail never requires it.
        services.AddSingleton<IHcgVpn, NoHcgVpn>();
        services.AddSingleton<OutboxService>();
        services.AddSingleton<ConnectionRecoveryService>();
        services.AddSingleton<IHtmlToTextConverter, HtmlToTextConverter>();
        services.AddSingleton<IHtmlSanitizer, MailHtmlSanitizer>();
        // Messages are laid out at phone width: the sender's phone breakpoints (@media max-width) apply, as in Apple Mail.
        MessageReaderViewModel.LayoutWidth = 420;

        // A store release offers only sign-in providers that are configured in this build (Microsoft needs a client ID;
        // Google has no iOS client yet). Their implementation stays in place for later activation.
        services.AddSingleton(platform.Capabilities);
        services.AddSingleton(new SignInPresentation(OfferUnconfiguredProviders: false, platform.Capabilities.MicrosoftSignIn, platform.Capabilities.GoogleSignIn));
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
