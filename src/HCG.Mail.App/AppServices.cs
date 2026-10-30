using HCG.Mail.App.Preview;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using HCG.Mail.Core.Settings;
using HCG.Mail.Email;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Credentials;
using HCG.Mail.Infrastructure.Logging;
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
        }
        else
        {
            services.AddSingleton(_ => CredentialVaultFactory.CreateForCurrentPlatform());
            services.AddSingleton<IAccountStore, JsonAccountStore>();
            services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
            services.AddSingleton<IMailSessionFactory, MailKitMailSessionFactory>();
            services.AddSingleton<IConnectionTester, ConnectionTester>();
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
        services.AddSingleton<MainWindowViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
