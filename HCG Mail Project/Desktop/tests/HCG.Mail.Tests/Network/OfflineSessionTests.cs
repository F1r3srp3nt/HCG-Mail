using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Offline;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Storage;
using HCG.Mail.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Network;

public sealed class OfflineSessionTests : IDisposable
{
    private static readonly MailAccount Account = new()
    {
        Id = "offline-test",
        DisplayName = "Student",
        EmailAddress = "student@example.no",
        Username = "student@example.no",
        Imap = new ServerEndpoint("imap.example.no", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("smtp.example.no", 465, MailSecurityMode.SslTls),
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("hcg-mail-offline-").FullName;
    private readonly JsonMailCache _cache;
    private readonly ManualNetworkMonitor _network = new();
    private readonly SwitchableSession _inner = new(Account);
    private readonly OfflineCapableMailSession _session;

    public OfflineSessionTests()
    {
        _cache = new JsonMailCache(new AppPaths(_dir), () => OfflineCachePolicy.Default,
            new HCG.Mail.Infrastructure.Security.AccountDataProtector(new HCG.Mail.Infrastructure.Credentials.SessionCredentialVault(), NullLogger<HCG.Mail.Infrastructure.Security.AccountDataProtector>.Instance),
            NullLogger<JsonMailCache>.Instance);
        _session = new OfflineCapableMailSession(_inner, _cache, _network, NullLogger.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task ReadEverythingOnlineAsync()
    {
        await _session.ConnectAsync();
        await _session.GetFoldersAsync();
        await _session.GetMessagesAsync("INBOX", 0, 50);
        await _session.GetMessageAsync(SwitchableSession.Msg);
    }

    [Fact]
    public async Task Mail_read_online_stays_readable_when_the_server_cannot_be_reached()
    {
        await ReadEverythingOnlineAsync();
        _inner.FailWith = MailErrorKind.Timeout;

        var page = await _session.GetMessagesAsync("INBOX", 0, 50);
        Assert.True(_session.IsOffline);
        Assert.Equal(MailErrorKind.Timeout, _session.OfflineReason!.Kind);
        Assert.Equal("Lesson plan", Assert.Single(page.Messages).Subject);
        Assert.NotNull(_session.CopySavedAt);

        // Further reads come straight from the copy: no more waiting for timeouts on every click.
        var calls = _inner.Calls;
        var folders = await _session.GetFoldersAsync();
        var message = await _session.GetMessageAsync(SwitchableSession.Msg);
        Assert.Equal(calls, _inner.Calls);
        Assert.Equal(FolderRole.Inbox, Assert.Single(folders).Role);
        Assert.Equal("Bring the lab notebook.", message.TextBody);
        Assert.Equal([1, 2, 3], Assert.Single(message.InlineResources).Data);

        // Local search over the copy.
        Assert.Single(await _session.SearchAsync("INBOX", "room 204", 10));
    }

    [Fact]
    public async Task Changes_and_sending_fail_at_once_with_a_clear_message_while_offline()
    {
        await ReadEverythingOnlineAsync();
        _inner.FailWith = MailErrorKind.Network;
        await Assert.ThrowsAsync<MailOperationException>(() => _session.GetFoldersAsync().ContinueWith(_ => _session.SetFlagsAsync([SwitchableSession.Msg], MessageFlags.Seen, true)).Unwrap());

        var calls = _inner.Calls;
        var send = await Assert.ThrowsAsync<MailOperationException>(() => _session.SendAsync(new OutgoingMessage(), true));
        Assert.Contains("You are offline", send.UserMessage);
        Assert.False(send.OutcomeUnknown);
        var attachment = await Assert.ThrowsAsync<MailOperationException>(() => _session.GetAttachmentBytesAsync(SwitchableSession.Msg, 0));
        Assert.Contains("Attachments are downloaded from the server", attachment.UserMessage);
        Assert.Equal(calls, _inner.Calls);
    }

    [Fact]
    public async Task Only_a_successful_connection_brings_the_session_back_online()
    {
        await ReadEverythingOnlineAsync();
        _inner.FailWith = MailErrorKind.Dns;
        await _session.GetFoldersAsync();
        Assert.True(_session.IsOffline);

        await Assert.ThrowsAsync<MailOperationException>(() => _session.ConnectAsync());
        Assert.True(_session.IsOffline);

        var changes = 0;
        _session.OfflineChanged += (_, _) => changes++;
        _inner.FailWith = null;
        await _session.ConnectAsync();
        Assert.False(_session.IsOffline);
        Assert.Null(_session.OfflineReason);
        Assert.Equal(1, changes);
        await _session.SendAsync(new OutgoingMessage { Subject = "Back" }, false);
        Assert.Single(_inner.Sent);
    }

    [Fact]
    public async Task Nothing_saved_yet_gives_an_honest_message()
    {
        _inner.FailWith = MailErrorKind.Timeout;
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => _session.GetMessagesAsync("INBOX", 0, 50));
        Assert.Contains("has not been saved on this device yet", ex.UserMessage);
        Assert.Contains("Simulated Timeout", ex.UserMessage);
    }

    [Fact]
    public async Task Without_any_network_the_server_is_not_even_tried()
    {
        await ReadEverythingOnlineAsync();
        var calls = _inner.Calls;
        _network.Raise(NetworkEventKind.Unavailable);
        await Assert.ThrowsAsync<MailOperationException>(() => _session.ConnectAsync());
        var page = await _session.GetMessagesAsync("INBOX", 0, 50);
        Assert.Single(page.Messages);
        Assert.Equal(calls, _inner.Calls);
        Assert.True(_session.IsOffline);
    }

    [Theory]
    [InlineData(MailErrorKind.Authentication)]
    [InlineData(MailErrorKind.ServerRejected)]
    public async Task A_refused_password_or_request_is_not_mistaken_for_being_offline(MailErrorKind kind)
    {
        await ReadEverythingOnlineAsync();
        _inner.FailWith = kind;
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => _session.GetMessagesAsync("INBOX", 0, 50));
        Assert.Equal(kind, ex.Kind);
        Assert.False(_session.IsOffline);
    }

    [Fact]
    public async Task Flag_changes_and_deletions_are_reflected_in_the_copy()
    {
        await ReadEverythingOnlineAsync();
        await _session.SetFlagsAsync([SwitchableSession.Msg], MessageFlags.Seen | MessageFlags.Flagged, add: true);
        var page = _cache.LoadPage(Account.Id, "INBOX")!.Value;
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, Assert.Single(page.Messages).Flags);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, _cache.LoadMessage(Account.Id, SwitchableSession.Msg)!.Value.Flags);

        await _session.DeleteAsync(SwitchableSession.Msg, permanent: false);
        Assert.Empty(_cache.LoadPage(Account.Id, "INBOX")!.Value.Messages);
        Assert.Null(_cache.LoadMessage(Account.Id, SwitchableSession.Msg));
    }

    [Fact]
    public async Task Stale_marks_reach_the_live_connection() // after sleep or a network change
    {
        _session.MarkConnectionStale();
        Assert.Equal(1, _inner.StaleMarks);
        await Task.CompletedTask;
    }
}
