using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Email.Deliverability;

namespace HCG.Mail.App.ViewModels;

public sealed class MailHealthViewModelFactory(IMailDns dns, IDeliveryLog deliveryLog, ILinkOpener links, IDialogService dialogs,
    Core.Services.IConnectionTester tester, Core.Security.ICredentialVault vault, Infrastructure.AppPaths paths)
{
    /// <summary>Deliverability only (no live account), e.g. for an account that is not loaded.</summary>
    public MailHealthViewModel Create(MailAccount account) =>
        new(account, new MailHealthService(dns), new ReceivedMessageAnalyzer(dns), deliveryLog, links, dialogs);

    /// <summary>Connection health of the live account, plus deliverability.</summary>
    public MailHealthViewModel Create(AccountViewModel account) =>
        new(account.Account, new MailHealthService(dns), new ReceivedMessageAnalyzer(dns), deliveryLog, links, dialogs)
        {
            Connection = new ConnectionHealthViewModel(account, tester, vault, deliveryLog, paths),
        };
}
