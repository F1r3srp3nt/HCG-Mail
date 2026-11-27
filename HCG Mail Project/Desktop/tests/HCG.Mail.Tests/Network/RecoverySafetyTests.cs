using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Services;
using HCG.Mail.Email;
using HCG.Mail.Email.Html;
using HCG.Mail.Email.Imap;
using HCG.Mail.Email.Offline;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Network;

/// <summary>
/// Recovery never weakens security: however often a connection is retried (network changes, wake-up, Reconnect), a server
/// whose certificate does not verify, or that offers no encryption, never receives the password or a message, and HCG Mail
/// never falls back to an unencrypted connection.
/// </summary>
public sealed class RecoverySafetyTests
{
    private static MailAccount Account(int imapPort, int smtpPort, MailSecurityMode smtpSecurity = MailSecurityMode.SslTls) => new()
    {
        Id = "recovery-safety",
        DisplayName = "Student",
        EmailAddress = "student@example.no",
        Username = "student@example.no",
        Imap = new ServerEndpoint("127.0.0.1", imapPort, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("127.0.0.1", smtpPort, smtpSecurity),
    };

    private static IMailSession Session(MailAccount account) =>
        new OfflineCapableMailSession(
            new MailKitMailSessionFactory(new MailClientFactory { Timeout = TimeSpan.FromSeconds(5) }, new HtmlToTextConverter(), NullLoggerFactory.Instance)
                .Create(account, "secret-password-123"),
            new NoMailCache(), new ManualNetworkMonitor(), NullLogger.Instance);

    [Fact]
    public async Task Repeated_recovery_attempts_against_an_untrusted_certificate_never_fall_back_or_send_the_password()
    {
        using var server = new RecordingTlsServer();
        var account = Account(server.Port, server.Port);
        for (var attempt = 0; attempt < 4; attempt++) // e.g. Reconnect, a network change, wake-up, another network change
        {
            await using var session = Session(account);
            var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
            Assert.True(ex.Kind is MailErrorKind.Certificate or MailErrorKind.Tls, ex.Kind.ToString());
            Assert.NotEqual(RecoveryMode.Backoff, ReconnectPolicy.ModeFor(ex.Kind)); // never retried on a timer
            var send = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(new OutgoingMessage
            {
                To = [new EmailAddress(null, "kari@example.no")], Subject = "x", TextBody = "secret body",
            }, saveCopyToSent: false));
            Assert.False(send.OutcomeUnknown);
        }
        await Task.Delay(300);
        Assert.NotEmpty(server.FirstBytes);
        Assert.All(server.FirstBytes, b => Assert.Equal(0x16, b)); // every connection began with a TLS handshake, none in plain text
        Assert.Equal(0, server.ApplicationBytes);                   // no command, password or message ever crossed it
    }

    [Fact]
    public async Task A_server_that_offers_no_STARTTLS_never_receives_credentials_or_a_message()
    {
        using var smtp = new PlainSmtpServer();
        using var imap = new RecordingTlsServer();
        var account = Account(imap.Port, smtp.Port, MailSecurityMode.StartTls);
        await using var session = Session(account);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(new OutgoingMessage
            {
                To = [new EmailAddress(null, "kari@example.no")], Subject = "x", TextBody = "secret body",
            }, saveCopyToSent: false));
            Assert.False(ex.OutcomeUnknown);
        }
        await Task.Delay(200);
        Assert.True(smtp.Connections > 0);
        Assert.DoesNotContain(smtp.Received, l => l.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) || l.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(smtp.Received, l => l.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A TLS server with an untrusted self-signed certificate; records the first byte of every connection and any application data.</summary>
    private sealed class RecordingTlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private int _bytes;

        public RecordingTlsServer()
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=untrusted.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            _cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                    _ = Task.Run(() => HandleAsync(client));
                }
            });
        }

        public int Port { get; }
        public List<byte> FirstBytes { get; } = [];
        public int ApplicationBytes => Volatile.Read(ref _bytes);

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = new PeekStream(client.GetStream());
                var first = await stream.PeekByteAsync();
                if (first < 0)
                    return;
                lock (FirstBytes)
                    FirstBytes.Add((byte)first);
                using var ssl = new SslStream(stream);
                try
                {
                    await ssl.AuthenticateAsServerAsync(_cert);
                    var buffer = new byte[1024];
                    int n;
                    while ((n = await ssl.ReadAsync(buffer)) > 0)
                        Interlocked.Add(ref _bytes, n);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _cert.Dispose();
        }
    }

    /// <summary>An SMTP server that offers AUTH but no STARTTLS, and records every line it receives.</summary>
    private sealed class PlainSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _connections;

        public PlainSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => HandleAsync(client));
                }
            });
        }

        public int Port { get; }
        public int Connections => Volatile.Read(ref _connections);
        public List<string> Received { get; } = [];

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 plain.example ESMTP");
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        lock (Received)
                            Received.Add(line);
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                            await writer.WriteAsync("250-plain.example\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n");
                        else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("221 bye");
                            return;
                        }
                        else
                            await writer.WriteLineAsync("250 ok");
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>Lets the test server look at the first byte of a connection before TLS takes over the stream.</summary>
    private sealed class PeekStream(Stream inner) : Stream
    {
        private int _peeked = -1;

        public async Task<int> PeekByteAsync()
        {
            var one = new byte[1];
            var n = await inner.ReadAsync(one);
            _peeked = n == 0 ? -1 : one[0];
            return _peeked;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_peeked >= 0 && buffer.Length > 0)
            {
                buffer.Span[0] = (byte)_peeked;
                _peeked = -1;
                return 1;
            }
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
