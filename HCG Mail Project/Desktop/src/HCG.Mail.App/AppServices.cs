using HCG.Mail.Infrastructure.Network;
using HCG.Mail.Email.Offline;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Network;
using HCG.Mail.App.Preview;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Release;
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
using HCG.Mail.Infrastructure.Google;
using HCG.Mail.Infrastructure.Microsoft;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App;

/// <summary>Composition root. Preview mode swaps every persistent or networked service for an in-memory one.</summary>
public static class AppServices
{
    /// <summary>This build's release capabilities (from -p:HcgReleaseChannel). Tests may replace it.</summary>
    public static ReleaseCapabilities Capabilities { get; set; } = ReleaseCapabilities.FromAssembly(typeof(AppServices).Assembly);

    public static ServiceProvider Build(AppMode mode, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        var paths = AppPaths.ForCurrentUser();

        services.AddSingleton(mode);
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            if (!mode.IsPreview)
            {
                try
                {
                    paths.EnsureCreated();
                    builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Continue without file logging if the profile directory is unavailable.
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
            services.AddSingleton<IGoogleAuth>(new UnconfiguredGoogleAuth("Google sign-in is not available in development preview mode."));
        }
        else
        {
            services.AddSingleton(_ => CredentialVaultFactory.CreateForCurrentPlatform());
            services.AddSingleton<IAccountStore, JsonAccountStore>();
            services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
            services.AddSingleton<MailSessionFactory>();
            services.AddSingleton<INetworkMonitor, SystemNetworkMonitor>();
            // Offline copies and the Outbox are encrypted per account; the keys live only in the credential store.
            services.AddSingleton<HCG.Mail.Infrastructure.Security.AccountDataProtector>();
            services.AddSingleton<IMailCache>(sp => new JsonMailCache(sp.GetRequiredService<AppPaths>(),
                () => sp.GetRequiredService<AppSettingsService>().Current.GetOfflinePolicy(),
                sp.GetRequiredService<HCG.Mail.Infrastructure.Security.AccountDataProtector>(), sp.GetRequiredService<ILogger<JsonMailCache>>()));
            // Without a persistent credential store the keys would be lost at exit, so the Outbox then lives for this session.
            services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<ICredentialVault>().IsPersistent
                ? ActivatorUtilities.CreateInstance<JsonOutboxStore>(sp)
                : new InMemoryOutboxStore());
            // Every session keeps mail readable offline and fails fast while the server cannot be reached.
            services.AddSingleton<IMailSessionFactory>(sp => new OfflineCapableSessionFactory(sp.GetRequiredService<MailSessionFactory>(),
                sp.GetRequiredService<IMailCache>(), sp.GetRequiredService<INetworkMonitor>(), sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IMicrosoftAuth>(sp =>
            {
                // A build without the Microsoft capability never activates Microsoft sign-in, whatever is configured.
                if (!Capabilities.MicrosoftSignIn)
                    return new UnconfiguredMicrosoftAuth(ReleaseCapabilities.MicrosoftUnavailableMessage);
                var clientId = MicrosoftAuthOptions.ResolveClientId(typeof(AppServices).Assembly);
                return MicrosoftAuthOptions.IsValidClientId(clientId)
                    ? new MsalMicrosoftAuth(clientId!, sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<MsalMicrosoftAuth>>())
                    : new UnconfiguredMicrosoftAuth(MicrosoftAuthOptions.NotConfiguredMessage);
            });
            services.AddSingleton<IGoogleAuth>(sp =>
            {
                if (!Capabilities.GoogleSignIn)
                    return new UnconfiguredGoogleAuth(ReleaseCapabilities.GoogleUnavailableMessage);
                var clientId = GoogleAuthOptions.ResolveClientId(typeof(AppServices).Assembly);
                var clientSecret = GoogleAuthOptions.ResolveClientSecret(typeof(AppServices).Assembly);
                return GoogleAuthOptions.ConfigurationProblem(clientId, clientSecret) is { } problem
                    ? new UnconfiguredGoogleAuth(problem)
                    : new GoogleOAuth(clientId!, clientSecret!, sp.GetRequiredService<ICredentialVault>(), sp.GetRequiredService<ILogger<GoogleOAuth>>());
            });
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
        services.AddSingleton(sp =>
        {
            // An HCG-operated connectivity endpoint is optional: offered only when the build or environment names one.
            var hcg = HcgConnectivityEndpoint.Resolve(typeof(AppServices).Assembly);
            var settings = sp.GetRequiredService<AppSettingsService>();
            return new ConnectivityProviderSelection(() => ConnectivityProviders.Resolve(settings.Current.ConnectivityCheckProvider, hcg),
                ConnectivityProviders.Available(hcg));
        });
        services.AddSingleton<NetworkDoctor>();
        // Optional: a host that ships HCG VPN registers its own IHcgVpn. HCG Mail never requires it.
        services.AddSingleton<IHcgVpn, NoHcgVpn>();
        services.AddSingleton<OutboxService>();
        services.AddSingleton<ConnectionRecoveryService>();
        services.AddSingleton<IHtmlToTextConverter, HtmlToTextConverter>();
        services.AddSingleton<IHtmlSanitizer, MailHtmlSanitizer>();

        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<ILinkOpener, LinkOpener>();
        services.AddSingleton<RemoteImageLoader>();
        services.AddSingleton<MailHealthViewModelFactory>();
        // Sign-in providers this build is not configured for (no client ID) are not offered: their buttons are hidden and
        // Settings says the sign-in is not set up in this build. Configured providers are always offered.
        services.AddSingleton(Capabilities);
        services.AddSingleton(new SignInPresentation(OfferUnconfiguredProviders: false, Capabilities.MicrosoftSignIn, Capabilities.GoogleSignIn));
        services.AddSingleton<MainWindowViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
