using System.Net;
using HCG.Mail.Email.Deliverability;

namespace HCG.Mail.App.Preview;

/// <summary>Development preview mode makes no network requests: every DNS check reports "not tested".</summary>
public sealed class OfflineMailDns : IMailDns
{
    private const string Reason = "development preview mode makes no DNS queries";

    public Task<DnsAnswer<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(DnsAnswer<string>.Failed(Reason));
    public Task<DnsAnswer<string>> GetMxAsync(string domain, CancellationToken cancellationToken = default) => Task.FromResult(DnsAnswer<string>.Failed(Reason));
    public Task<DnsAnswer<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(DnsAnswer<IPAddress>.Failed(Reason));
    public Task<DnsAnswer<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken = default) => Task.FromResult(DnsAnswer<string>.Failed(Reason));
}
