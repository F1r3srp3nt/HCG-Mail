using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Email.Deliverability;

namespace HCG.Mail.App.ViewModels;

public sealed class MailHealthViewModelFactory(IMailDns dns, IDeliveryLog deliveryLog, ILinkOpener links, IDialogService dialogs)
{
    public MailHealthViewModel Create(MailAccount account) =>
        new(account, new MailHealthService(dns), new ReceivedMessageAnalyzer(dns), deliveryLog, links, dialogs);
}
