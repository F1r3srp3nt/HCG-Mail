using System.Net;
using HCG.Mail.Email.Deliverability;

namespace HCG.Mail.Tests.Email;

/// <summary>In-memory DNS for deliverability tests. Unknown names are NXDOMAIN; names in <see cref="Broken"/> fail.</summary>
internal sealed class FakeMailDns : IMailDns
{
    public Dictionary<string, List<string>> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Mx { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<IPAddress>> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Ptr { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Queries { get; } = [];

    public FakeMailDns AddTxt(string name, params string[] records)
    {
        if (!Txt.TryGetValue(name, out var list))
            Txt[name] = list = [];
        list.AddRange(records);
        return this;
    }

    public FakeMailDns AddA(string name, params string[] ips)
    {
        if (!Addresses.TryGetValue(name, out var list))
            Addresses[name] = list = [];
        list.AddRange(ips.Select(IPAddress.Parse));
        return this;
    }

    public Task<DnsAnswer<string>> GetTxtAsync(string name, CancellationToken cancellationToken = default) => Answer(name, Txt);
    public Task<DnsAnswer<string>> GetMxAsync(string domain, CancellationToken cancellationToken = default) => Answer(domain, Mx);
    public Task<DnsAnswer<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken = default) => Answer(name, Addresses);
    public Task<DnsAnswer<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken = default) => Answer(address.ToString(), Ptr);

    private Task<DnsAnswer<T>> Answer<T>(string name, Dictionary<string, List<T>> table)
    {
        Queries.Add(name);
        if (Broken.Contains(name))
            return Task.FromResult(DnsAnswer<T>.Failed("SERVFAIL"));
        return Task.FromResult(table.TryGetValue(name, out var records) ? DnsAnswer<T>.Found(records) : DnsAnswer<T>.NotFound());
    }
}
