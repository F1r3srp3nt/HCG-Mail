using System.Net;
using System.Net.Sockets;

namespace HCG.Mail.Email.Diagnostics;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface ITcpProbe
{
    Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        IPAddress.TryParse(host, out var ip) ? Task.FromResult(new[] { ip }) : Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class SystemTcpProbe : ITcpProbe
{
    public async Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SocketException((int)SocketError.TimedOut);
        }
    }
}
