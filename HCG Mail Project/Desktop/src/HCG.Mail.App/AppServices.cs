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
using HCG.Mail.Infrastructure.Google;
using HCG.Mail.Infrastructure.Microsoft;
using HCG.Mail.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App;

/// <summary>Composition root. Preview mode swaps every persistent or networked service for an in-memory one.</summary>
public static class AppServices
{
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
            services.AddSingleton<IMailSessionFactory, PreviewMailSessionFactory>();
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
            services.AddSingleton<IMailSessionFactory, MailSessionFactory>();
            services.AddSingleton<IMicrosoftAuth>(sp =>
            {
                var clientId = MicrosoftAuthOptions.ResolveClientId(typeof(AppServices).Assembly);
                return MicrosoftAuthOptions.IsValidClientId(clientId)
                    ? new MsalMicrosoftAuth(clientId!, sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<MsalMicrosoftAuth>>())
                    : new UnconfiguredMicrosoftAuth(MicrosoftAuthOptions.NotConfiguredMessage);
            });
            services.AddSingleton<IGoogleAuth>(sp =>
            {
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
        services.AddSingleton<MainWindowViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
