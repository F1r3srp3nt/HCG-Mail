using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Network;
using HCG.Mail.Email.Deliverability;
using HCG.Mail.Email.Diagnostics;

namespace HCG.Mail.App.ViewModels;

public sealed class MailHealthViewModelFactory(IMailDns dns, IDeliveryLog deliveryLog, ILinkOpener links, IDialogService dialogs,
    Core.Services.IConnectionTester tester, Core.Security.ICredentialVault vault, Infrastructure.AppPaths paths,
    NetworkDoctor? networkDoctor = null, INetworkMonitor? networkMonitor = null, IHcgVpn? hcgVpn = null)
{
    /// <summary>Deliverability only (no live account), e.g. for an account that is not loaded.</summary>
    public MailHealthViewModel Create(MailAccount account) =>
        new(account, new MailHealthService(dns), new ReceivedMessageAnalyzer(dns), deliveryLog, links, dialogs);

    /// <summary>Connection health and the Network Doctor of the live account, plus deliverability.</summary>
    public MailHealthViewModel Create(AccountViewModel account, Func<AccountViewModel, ServerEndpoint, Task>? applySmtp = null, OutboxService? outbox = null) =>
        new(account.Account, new MailHealthService(dns), new ReceivedMessageAnalyzer(dns), deliveryLog, links, dialogs)
        {
            Connection = new ConnectionHealthViewModel(account, tester, vault, deliveryLog, paths),
            Network = networkDoctor is null || account.UsesOAuth ? null
                : new NetworkDoctorViewModel(account, networkDoctor, vault, networkMonitor ?? new ManualNetworkMonitor(), hcgVpn ?? new NoHcgVpn(), outbox, applySmtp, dialogs),
        };
}
