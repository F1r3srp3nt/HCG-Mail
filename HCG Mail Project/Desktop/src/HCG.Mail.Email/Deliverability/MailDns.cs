using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace HCG.Mail.Email.Deliverability;

public enum DnsLookupStatus
{
    Found,

    /// <summary>The name does not exist or has no records of the requested type.</summary>
    NotFound,

    /// <summary>The lookup itself failed (timeout, server failure, no network) — nothing can be concluded.</summary>
    Failed,
}

public sealed record DnsAnswer<T>(DnsLookupStatus Status, IReadOnlyList<T> Records, string? Error = null)
{
    public static DnsAnswer<T> Found(IReadOnlyList<T> records) => new(records.Count > 0 ? DnsLookupStatus.Found : DnsLookupStatus.NotFound, records);
    public static DnsAnswer<T> NotFound() => new(DnsLookupStatus.NotFound, []);
    public static DnsAnswer<T> Failed(string error) => new(DnsLookupStatus.Failed, [], error);
}

/// <summary>The DNS lookups the deliverability checks need. Read-only; HCG Mail never changes DNS.</summary>
public interface IMailDns
{
    /// <summary>TXT records, each with its character-strings joined (long DKIM keys are split into several strings).</summary>
    Task<DnsAnswer<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default);

    Task<DnsAnswer<string>> GetMxAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>A and AAAA records.</summary>
    Task<DnsAnswer<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken = default);

    Task<DnsAnswer<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken = default);
}

/// <summary>Uses the computer's configured DNS servers (DnsClient.NET).</summary>
public sealed class SystemMailDns : IMailDns
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 1,
        UseCache = true,
        ContinueOnDnsError = false,
        ThrowDnsErrors = false,
    });

    public Task<DnsAnswer<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default) =>
        QueryAsync(name, QueryType.TXT, r => r.Answers.TxtRecords().Select(t => string.Concat(t.Text)).ToList(), cancellationToken);

    public Task<DnsAnswer<string>> GetMxAsync(string domain, CancellationToken cancellationToken = default) =>
        QueryAsync(domain, QueryType.MX, r => r.Answers.MxRecords().OrderBy(m => m.Preference).Select(m => m.Exchange.Value.TrimEnd('.')).ToList(), cancellationToken);

    public async Task<DnsAnswer<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken = default)
    {
        var v4 = await QueryAsync(name, QueryType.A, r => r.Answers.ARecords().Select(a => a.Address).ToList(), cancellationToken).ConfigureAwait(false);
        var v6 = await QueryAsync(name, QueryType.AAAA, r => r.Answers.AaaaRecords().Select(a => a.Address).ToList(), cancellationToken).ConfigureAwait(false);
        if (v4.Status == DnsLookupStatus.Failed && v6.Status == DnsLookupStatus.Failed)
            return v4;
        return DnsAnswer<IPAddress>.Found([.. v4.Records, .. v6.Records]);
    }

    public Task<DnsAnswer<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        QueryAsync(address.GetArpaName(), QueryType.PTR, r => r.Answers.PtrRecords().Select(p => p.PtrDomainName.Value.TrimEnd('.')).ToList(), cancellationToken);

    private async Task<DnsAnswer<T>> QueryAsync<T>(string name, QueryType type, Func<IDnsQueryResponse, List<T>> select, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.QueryAsync(name, type, QueryClass.IN, cancellationToken).ConfigureAwait(false);
            if (response.HasError)
            {
                return response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain
                    ? DnsAnswer<T>.NotFound()
                    : DnsAnswer<T>.Failed($"DNS error {response.Header.ResponseCode}");
            }
            return DnsAnswer<T>.Found(select(response));
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException or System.Net.Sockets.SocketException or ArgumentException)
        {
            return DnsAnswer<T>.Failed(ex is OperationCanceledException ? "the lookup timed out" : ex.Message);
        }
    }
}
