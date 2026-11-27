using System.Net;
using System.Net.Sockets;
using HCG.Mail.Core.Accounts;
using MailKit.Net.Smtp;

namespace HCG.Mail.Email.Smtp;

/// <summary>
/// Opens SMTP connections with a standards-compliant EHLO name.
/// </summary>
/// <remarks>
/// RFC 5321 §4.1.1.1 requires the EHLO argument to be the client's fully qualified domain name or, when it has
/// none, an address literal. Left alone, MailKit 4.18 sends the computer's bare host name (for example
/// <c>EHLO Yasins-MacBook-Pro</c>), which is neither, and the provider copies it into the <c>Received:</c>
/// header of every message — exposing the computer's name and giving spam filters a malformed HELO to score.
/// Desktop computers have no public FQDN, so HCG Mail uses the address literal of the local end of the actual
/// connection (<c>EHLO [192.168.1.20]</c>), as Apple Mail and Thunderbird do.
/// </remarks>
public static class SmtpConnector
{
    public static async Task ConnectAsync(ISmtpClient smtp, ServerEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (smtp is not SmtpClient)
        {
            // Other ISmtpClient implementations (test doubles) own their transport; connect them normally.
            await smtp.ConnectAsync(endpoint.Host, endpoint.Port, SecurityOptions.ToSecureSocketOptions(endpoint.Security), cancellationToken).ConfigureAwait(false);
            return;
        }

        var timeout = smtp.Timeout > 0 ? TimeSpan.FromMilliseconds(smtp.Timeout) : TimeSpan.FromSeconds(45);
        var socket = await OpenSocketAsync(endpoint.Host, endpoint.Port, timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            smtp.LocalDomain = EhloName(socket.LocalEndPoint);
            // The client takes ownership of the connected socket; certificate validation still uses endpoint.Host.
            await smtp.ConnectAsync(socket, endpoint.Host, endpoint.Port, SecurityOptions.ToSecureSocketOptions(endpoint.Security), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>RFC 5321 address literal for the local end of the connection: <c>[192.0.2.1]</c> or <c>[IPv6:2001:db8::1]</c>.</summary>
    public static string EhloName(EndPoint? localEndPoint)
    {
        if (localEndPoint is not IPEndPoint { Address: var address })
            return "[127.0.0.1]";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var withoutScope = new IPAddress(address.GetAddressBytes()); // a zone index ("%en0") is not valid in an address literal
            return $"[IPv6:{withoutScope}]";
        }
        return $"[{address}]";
    }

    private static async Task<Socket> OpenSocketAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), timeoutSource.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Connecting to {host}:{port} timed out after {timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
