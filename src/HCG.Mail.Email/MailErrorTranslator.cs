using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Errors;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace HCG.Mail.Email;

public enum MailProtocol
{
    Imap,
    Smtp,
}

/// <summary>Converts low-level networking and protocol exceptions into actionable messages for the user.</summary>
public static class MailErrorTranslator
{
    public static MailOperationException Translate(Exception ex, MailProtocol protocol, ServerEndpoint endpoint, TlsValidationRecorder? recorder = null)
    {
        if (ex is MailOperationException moe)
            return moe;
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg)
            return Translate(agg.InnerExceptions[0], protocol, endpoint, recorder);

        var server = protocol == MailProtocol.Imap ? "incoming (IMAP)" : "outgoing (SMTP)";
        var host = endpoint.Host;

        switch (ex)
        {
            case OperationCanceledException:
                return new(MailErrorKind.Cancelled, "The operation was cancelled.", ex);

            case SocketException se when se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain:
                return new(MailErrorKind.Dns, $"The {server} server '{host}' could not be found. Check the server name and your internet connection.", ex);

            case SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused:
                return new(MailErrorKind.Network, $"'{host}' refused the connection on port {endpoint.Port}. Check the port number and security setting.", ex);

            case SocketException se when se.SocketErrorCode is SocketError.TimedOut:
                return new(MailErrorKind.Timeout, $"Connecting to '{host}' on port {endpoint.Port} timed out. A firewall may be blocking the port, or the server may be unavailable.", ex);

            case SocketException se when se.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown:
                return new(MailErrorKind.Network, "No network connection is available. Check your internet connection.", ex);

            case SocketException:
                return new(MailErrorKind.Network, $"A network error occurred while talking to '{host}'.", ex);

            case TimeoutException:
                return new(MailErrorKind.Timeout, $"The {server} server '{host}' did not respond in time.", ex);

            case SslHandshakeException:
                return TranslateTls(ex, server, endpoint, recorder);

            case System.Security.Authentication.AuthenticationException when recorder?.HasFailure == true:
                return TranslateTls(ex, server, endpoint, recorder);

            case MailKit.Security.AuthenticationException:
                return new(MailErrorKind.Authentication, $"The {server} server rejected the username or password.", ex);

            case NotSupportedException when ex.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase):
                return new(MailErrorKind.Tls,
                    $"'{host}' does not offer STARTTLS on port {endpoint.Port}. HCG Mail never connects without encryption — try SSL/TLS on port {(protocol == MailProtocol.Imap ? PortDefaults.ImapSslTls : PortDefaults.SmtpSslTls)}.", ex);

            case ImapCommandException ice:
                return new(MailErrorKind.ServerRejected, $"The server refused the request: {Clean(ice.ResponseText)}", ex);

            case SmtpCommandException sce:
                return TranslateSmtpCommand(sce);

            case FolderNotFoundException:
                return new(MailErrorKind.FolderNotFound, "The folder no longer exists on the server. Refresh the folder list.", ex);

            case MessageNotFoundException:
                return new(MailErrorKind.MessageNotFound, "The message no longer exists on the server. It may have been moved or deleted elsewhere.", ex);

            case ImapProtocolException or SmtpProtocolException when LooksLikeTlsMismatch(endpoint):
                return new(MailErrorKind.Protocol,
                    $"The server on port {endpoint.Port} did not respond as expected. Check that the security setting matches the port (SSL/TLS: {(protocol == MailProtocol.Imap ? "993" : "465")}, STARTTLS: {(protocol == MailProtocol.Imap ? "143" : "587")}).", ex);

            case ImapProtocolException or SmtpProtocolException:
                return new(MailErrorKind.Protocol, $"The connection to '{host}' was interrupted or the server sent an unexpected response.", ex);

            case ServiceNotConnectedException or ServiceNotAuthenticatedException or IOException:
                return new(MailErrorKind.Network, $"The connection to '{host}' was lost. HCG Mail will reconnect on the next action.", ex);

            case NotSupportedException:
                return new(MailErrorKind.NotSupported, $"The server does not support this operation: {Clean(ex.Message)}", ex);

            default:
                return new(MailErrorKind.Unknown, $"An unexpected error occurred ({ex.GetType().Name}).", ex);
        }
    }

    private static MailOperationException TranslateTls(Exception ex, string server, ServerEndpoint endpoint, TlsValidationRecorder? recorder)
    {
        if (recorder?.HasFailure == true)
        {
            var errors = recorder.Errors!.Value;
            var cert = recorder.DescribeCertificate();
            string reason;
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                reason = "the server did not present a security certificate";
            else if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                reason = $"the certificate belongs to {cert}, which does not match '{endpoint.Host}'. Your provider may use a different server name";
            else if (recorder.ChainStatus.HasFlag(X509ChainStatusFlags.Revoked))
                reason = "the certificate has been revoked";
            else if (recorder.ChainStatus.HasFlag(X509ChainStatusFlags.NotTimeValid))
                reason = $"the certificate has expired or is not yet valid ({cert})";
            else if (recorder.ChainStatus.HasFlag(X509ChainStatusFlags.UntrustedRoot) || recorder.ChainStatus.HasFlag(X509ChainStatusFlags.PartialChain))
                reason = $"the certificate is not issued by a trusted authority ({cert}); it may be self-signed or intercepted";
            else if (recorder.ChainStatus.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown) || recorder.ChainStatus.HasFlag(X509ChainStatusFlags.OfflineRevocation))
                reason = "the certificate's revocation status could not be checked because the certificate authority could not be reached";
            else
                reason = $"the certificate could not be verified ({recorder.ChainStatus})";
            return new(MailErrorKind.Certificate, $"Secure connection to the {server} server refused: {reason}. HCG Mail does not connect to servers with untrusted certificates.", ex);
        }

        return new(MailErrorKind.Tls,
            $"A secure (TLS) connection to '{endpoint.Host}:{endpoint.Port}' could not be established. Check that the security setting matches the port.", ex);
    }

    private static MailOperationException TranslateSmtpCommand(SmtpCommandException ex) => ex.ErrorCode switch
    {
        SmtpErrorCode.RecipientNotAccepted => new(MailErrorKind.ServerRejected,
            $"The outgoing server rejected the recipient {ex.Mailbox?.Address}: {Clean(ex.Message)}", ex),
        SmtpErrorCode.SenderNotAccepted => new(MailErrorKind.ServerRejected,
            $"The outgoing server does not allow sending as {ex.Mailbox?.Address}: {Clean(ex.Message)}", ex),
        SmtpErrorCode.MessageNotAccepted => new(MailErrorKind.ServerRejected,
            $"The outgoing server rejected the message: {Clean(ex.Message)}", ex),
        _ => new(MailErrorKind.ServerRejected, $"The outgoing server returned an error: {Clean(ex.Message)}", ex),
    };

    private static bool LooksLikeTlsMismatch(ServerEndpoint endpoint) =>
        (endpoint.Security == MailSecurityMode.StartTls && endpoint.Port is PortDefaults.ImapSslTls or PortDefaults.SmtpSslTls)
        || (endpoint.Security == MailSecurityMode.SslTls && endpoint.Port is PortDefaults.ImapStartTls or PortDefaults.SmtpStartTls or 25);

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "no details were given.";
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
