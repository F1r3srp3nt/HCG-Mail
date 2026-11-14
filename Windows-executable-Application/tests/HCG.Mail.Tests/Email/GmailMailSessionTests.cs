using System.Net;
using System.Text;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Email.Gmail;
using HCG.Mail.Email.Html;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace HCG.Mail.Tests.Email;

/// <summary>Gmail mailbox workflow against a recording fake of gmail.googleapis.com (no network, no real tokens).</summary>
public sealed class GmailMailSessionTests
{
    private sealed class FakeAuth : IGoogleAuth
    {
        public int Version;
        public List<(string Key, bool Force)> Requests { get; } = [];
        public bool IsConfigured => true;
        public string? ConfigurationProblem => null;
        public IReadOnlyList<string> Scopes => [];
        public Task<GoogleSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken)
        {
            Requests.Add((accountKey, forceRefresh));
            if (forceRefresh)
                Version++;
            return Task.FromResult($"token-{accountKey}-v{Version}");
        }

        public Task SignOutAsync(string accountKey) => Task.CompletedTask;
    }

    private sealed record Seen(HttpMethod Method, string Path, string? Body, string? Authorization);

    private sealed class FakeGmail : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Prefix, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = [];
        public List<Seen> Seen { get; } = [];

        public FakeGmail On(HttpMethod method, string prefix, Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _routes.Add((method, prefix, reply));
            return this;
        }

        public FakeGmail Json(HttpMethod method, string prefix, object body, HttpStatusCode status = HttpStatusCode.OK) =>
            On(method, prefix, _ => Reply(status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("gmail.googleapis.com", request.RequestUri!.Host);
            var path = Uri.UnescapeDataString(request.RequestUri.PathAndQuery)["/gmail/v1/users/me".Length..];
            Seen.Add(new(request.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            for (var i = _routes.Count - 1; i >= 0; i--)
            {
                if (_routes[i].Method == request.Method && path.StartsWith(_routes[i].Prefix, StringComparison.Ordinal))
                    return _routes[i].Reply(request);
            }
            return Error(HttpStatusCode.NotFound, "notFound");
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string reason) =>
        Reply(status, new { error = new { code = (int)status, message = reason + " message", errors = new[] { new { reason } } } });

    private static readonly MailAccount Account = new()
    {
        Id = "g1", DisplayName = "Ola", EmailAddress = "ola@example.com", Username = "ola@example.com",
        Imap = MailAccount.GmailEndpoint, Smtp = MailAccount.GmailEndpoint, Provider = MailProviderKind.Gmail, GoogleAccountId = "sub-1",
    };

    private static (GmailMailSession Session, FakeGmail Gmail, FakeAuth Auth, InMemoryDeliveryLog Log) Create()
    {
        var gmail = new FakeGmail();
        var auth = new FakeAuth();
        var log = new InMemoryDeliveryLog();
        return (new GmailMailSession(Account, auth, new HtmlToTextConverter(), NullLogger.Instance, log, gmail, (_, _) => Task.CompletedTask), gmail, auth, log);
    }

    private static object Meta(string id, string subject, long ms, params string[] labels) => new
    {
        id, labelIds = labels, snippet = "Hello &amp; welcome", internalDate = ms.ToString(), sizeEstimate = 1234,
        payload = new
        {
            mimeType = subject.Contains("attach") ? "multipart/mixed" : "multipart/alternative",
            headers = new[]
            {
                new { name = "From", value = "=?utf-8?Q?K=C3=A5ri?= <kari@example.com>" },
                new { name = "To", value = "Ola <ola@example.com>" },
                new { name = "Subject", value = subject },
                new { name = "Message-ID", value = $"<{id}@example.com>" },
            },
        },
    };

    private static string RawOf(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static MimeMessage Decode(string? json)
    {
        using var doc = JsonDocument.Parse(json!);
        var raw = doc.RootElement.TryGetProperty("raw", out var r) ? r.GetString()! : doc.RootElement.GetProperty("message").GetProperty("raw").GetString()!;
        return MimeMessage.Load(new MemoryStream(GmailMailSession.FromBase64Url(raw)));
    }

    [Fact]
    public async Task Labels_become_folders_with_All_Mail_as_the_archive()
    {
        var (session, gmail, _, _) = Create();
        // Later routes win, so the specific /labels/{id} route is registered after the /labels list.
        gmail.Json(HttpMethod.Get, "/profile", new { emailAddress = "ola@example.com", messagesTotal = 420 })
            .Json(HttpMethod.Get, "/labels", new
            {
                labels = new object[]
                {
                    new { id = "INBOX", name = "INBOX", type = "system" }, new { id = "UNREAD", name = "UNREAD", type = "system" },
                    new { id = "Label_2", name = "Clients/Acme", type = "user" }, new { id = "Label_1", name = "Clients", type = "user" },
                },
            })
            .Json(HttpMethod.Get, "/labels/", new { messagesTotal = 10, messagesUnread = 2 });
        await session.ConnectAsync();
        var folders = await session.GetFoldersAsync();
        Assert.Equal(["INBOX", "DRAFT", "SENT", GmailMailSession.AllMail, "SPAM", "TRASH", "Label_1", "Label_2"], folders.Select(f => f.FullName));
        Assert.Equal([FolderRole.Inbox, FolderRole.Drafts, FolderRole.Sent, FolderRole.Archive, FolderRole.Junk, FolderRole.Trash],
            folders.Take(6).Select(f => f.Role));
        Assert.Equal(420, folders.Single(f => f.FullName == GmailMailSession.AllMail).TotalCount);
        var acme = folders.Single(f => f.FullName == "Label_2");
        Assert.Equal(("Acme", 1, 2), (acme.Name, acme.Depth, acme.UnreadCount));
        Assert.All(gmail.Seen, s => Assert.Equal("Bearer token-sub-1-v0", s.Authorization));
    }

    [Fact]
    public async Task Pages_follow_Gmail_page_tokens_and_summaries_are_decoded()
    {
        var (session, gmail, _, _) = Create();
        gmail.Json(HttpMethod.Get, "/labels/INBOX", new { messagesTotal = 3, messagesUnread = 1 })
            .Json(HttpMethod.Get, "/messages?maxResults=2&labelIds=INBOX", new { messages = new[] { new { id = "m3" }, new { id = "m2" } }, nextPageToken = "p2" })
            .Json(HttpMethod.Get, "/messages?maxResults=2&labelIds=INBOX&pageToken=p2", new { messages = new[] { new { id = "m1" } } })
            .Json(HttpMethod.Get, "/messages/m3?format=metadata", Meta("m3", "Newest with attachment", 3000, "INBOX", "UNREAD", "STARRED"))
            .Json(HttpMethod.Get, "/messages/m2?format=metadata", Meta("m2", "=?utf-8?B?w4ZyZW5k?=", 2000, "INBOX"))
            .Json(HttpMethod.Get, "/messages/m1?format=metadata", Meta("m1", "Oldest", 1000, "INBOX"));

        var first = await session.GetMessagesAsync("INBOX", 0, 2);
        Assert.Equal(["m3", "m2"], first.Messages.Select(m => m.Ref.ServerId));
        Assert.True(first.HasOlder);
        Assert.Equal((3, 1), (first.TotalCount, first.UnreadCount));
        var newest = first.Messages[0];
        Assert.Equal("Kåri", newest.From!.Name);
        Assert.Equal("Hello & welcome", newest.Preview);
        Assert.True(newest.HasAttachments);
        Assert.Equal(MessageFlags.Flagged, newest.Flags); // unread and starred
        Assert.Equal("Ærend", first.Messages[1].Subject);
        Assert.Equal(MessageFlags.Seen, first.Messages[1].Flags);

        var older = await session.GetMessagesAsync("INBOX", 2, 2);
        Assert.Equal(["m1"], older.Messages.Select(m => m.Ref.ServerId));
        Assert.False(older.HasOlder);
    }

    [Fact]
    public async Task Reading_uses_raw_MIME_and_the_shared_pipeline()
    {
        var (session, gmail, _, _) = Create();
        var mime = new MimeMessage { Subject = "Quarterly", Body = new BodyBuilder { HtmlBody = "<p>Hi <b>Ola</b></p>", TextBody = "Hi Ola" }.ToMessageBody() };
        mime.From.Add(new MailboxAddress("Kari", "kari@example.com"));
        gmail.Json(HttpMethod.Get, "/messages/m1?format=raw", new { id = "m1", labelIds = new[] { "INBOX", "UNREAD" }, raw = RawOf(mime) });
        var content = await session.GetMessageAsync(new MessageRef("INBOX", 0, 0, "m1"));
        Assert.Equal("Quarterly", content.Subject);
        Assert.Contains("<b>Ola</b>", content.HtmlBody);
        Assert.False(content.Flags.HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public async Task Read_star_archive_and_trash_map_to_Gmail_labels()
    {
        var (session, gmail, _, _) = Create();
        gmail.Json(HttpMethod.Post, "/messages/batchModify", new { })
            .Json(HttpMethod.Post, "/messages/m1/modify", new { id = "m1" })
            .Json(HttpMethod.Post, "/messages/m1/trash", new { id = "m1" })
            .Json(HttpMethod.Post, "/messages/m2/untrash", new { id = "m2" })
            .Json(HttpMethod.Post, "/messages/m2/modify", new { id = "m2" });
        var inbox = new MessageRef("INBOX", 0, 0, "m1");

        await session.SetFlagsAsync([inbox], MessageFlags.Seen, add: true);
        Assert.Contains("\"removeLabelIds\":[\"UNREAD\"]", gmail.Seen[^1].Body);
        await session.SetFlagsAsync([inbox], MessageFlags.Flagged, add: true);
        Assert.Contains("\"addLabelIds\":[\"STARRED\"]", gmail.Seen[^1].Body);

        await session.MoveAsync(inbox, GmailMailSession.AllMail); // archive = remove the Inbox label
        Assert.Equal("/messages/m1/modify", gmail.Seen[^1].Path);
        Assert.Contains("\"removeLabelIds\":[\"INBOX\"]", gmail.Seen[^1].Body);
        Assert.Contains("\"addLabelIds\":[]", gmail.Seen[^1].Body);

        await session.DeleteAsync(inbox, permanent: false);
        Assert.Equal((HttpMethod.Post, "/messages/m1/trash"), (gmail.Seen[^1].Method, gmail.Seen[^1].Path));

        await session.MoveAsync(new MessageRef("TRASH", 0, 0, "m2"), "INBOX"); // restore from Trash
        Assert.Equal("/messages/m2/untrash", gmail.Seen[^2].Path);
        Assert.Contains("\"addLabelIds\":[\"INBOX\"]", gmail.Seen[^1].Body);

        var count = gmail.Seen.Count;
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.DeleteAsync(new MessageRef("TRASH", 0, 0, "m3"), permanent: false));
        Assert.Equal(MailErrorKind.NotSupported, ex.Kind);
        Assert.Contains("30 days", ex.UserMessage);
        Assert.Equal(count, gmail.Seen.Count); // nothing sent: permanent deletion is outside the granted permission
    }

    [Fact]
    public async Task Sending_posts_raw_MIME_once_records_delivery_and_removes_the_draft()
    {
        var (session, gmail, _, log) = Create();
        gmail.Json(HttpMethod.Post, "/messages/send", new { id = "sent1", labelIds = new[] { "SENT" } })
            .Json(HttpMethod.Get, "/drafts?maxResults=500", new { drafts = new[] { new { id = "r-1", message = new { id = "d1" } } } })
            .On(HttpMethod.Delete, "/drafts/r-1", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var result = await session.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress("Kari", "kari@example.com")], Bcc = [new EmailAddress(null, "hidden@example.com")],
            Subject = "Hei", TextBody = "Body", DraftRef = new MessageRef("DRAFT", 0, 0, "d1"),
        }, saveCopyToSent: true);

        var sent = Decode(gmail.Seen.Single(s => s.Path == "/messages/send").Body);
        Assert.Equal("Hei", sent.Subject);
        Assert.Equal("hidden@example.com", sent.Bcc.Mailboxes.Single().Address);
        Assert.Equal(DeliveryState.Accepted, result.Delivery!.State);
        Assert.Equal(DeliveryState.Accepted, Assert.Single(log.GetRecent("g1")).State);
        Assert.Contains(gmail.Seen, s => s.Method == HttpMethod.Delete && s.Path == "/drafts/r-1");
    }

    [Fact]
    public async Task A_failed_send_is_not_retried_and_is_recorded()
    {
        var (session, gmail, _, log) = Create();
        gmail.On(HttpMethod.Post, "/messages/send", _ => Error(HttpStatusCode.TooManyRequests, "rateLimitExceeded"));
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(
            new OutgoingMessage { To = [new EmailAddress(null, "kari@example.com")], Subject = "x", TextBody = "y" }, true));
        Assert.Equal(MailErrorKind.Throttled, ex.Kind);
        Assert.Single(gmail.Seen, s => s.Path == "/messages/send");
        Assert.Equal(DeliveryState.Deferred, Assert.Single(log.GetRecent("g1")).State);
    }

    [Fact]
    public async Task Drafts_are_created_then_updated_in_place()
    {
        var (session, gmail, _, _) = Create();
        gmail.Json(HttpMethod.Post, "/drafts", new { id = "r-7", message = new { id = "d7", labelIds = new[] { "DRAFT" } } })
            .Json(HttpMethod.Put, "/drafts/r-7", new { id = "r-7", message = new { id = "d8", labelIds = new[] { "DRAFT" } } });
        var first = await session.SaveDraftAsync(new OutgoingMessage { Subject = "v1", TextBody = "a" });
        Assert.Equal(new MessageRef("DRAFT", 0, 0, "d7"), first.DraftRef);
        var second = await session.SaveDraftAsync(new OutgoingMessage { Subject = "v2", TextBody = "b", DraftRef = first.DraftRef });
        Assert.Equal("d8", second.DraftRef!.ServerId);
        Assert.Equal("v2", Decode(gmail.Seen[^1].Body).Subject);
        Assert.Equal(HttpMethod.Put, gmail.Seen[^1].Method);
    }

    [Fact]
    public async Task Expired_access_is_refreshed_once_then_reconnect_is_required()
    {
        var (session, gmail, auth, _) = Create();
        var calls = 0;
        gmail.On(HttpMethod.Get, "/profile", _ => ++calls == 1 ? Error(HttpStatusCode.Unauthorized, "authError") : Reply(HttpStatusCode.OK, new { emailAddress = "ola@example.com" }));
        await session.ConnectAsync();
        Assert.Equal([("sub-1", false), ("sub-1", true)], auth.Requests);

        gmail.On(HttpMethod.Get, "/profile", _ => Error(HttpStatusCode.Unauthorized, "authError"));
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
        Assert.Equal(MailErrorKind.ReconnectRequired, ex.Kind);

        gmail.On(HttpMethod.Get, "/profile", _ => Error(HttpStatusCode.Forbidden, "insufficientPermissions"));
        Assert.Equal(MailErrorKind.ReconnectRequired, (await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync())).Kind);
        gmail.On(HttpMethod.Get, "/profile", _ => Error(HttpStatusCode.Forbidden, "accessNotConfigured"));
        Assert.Equal(MailErrorKind.NotSupported, (await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync())).Kind);
    }

    [Fact]
    public async Task Rate_limited_reads_are_retried_but_writes_are_not()
    {
        var (session, gmail, _, _) = Create();
        var reads = 0;
        gmail.On(HttpMethod.Get, "/profile", _ => ++reads < 3 ? Error(HttpStatusCode.Forbidden, "userRateLimitExceeded") : Reply(HttpStatusCode.OK, new { emailAddress = "x" }));
        await session.ConnectAsync();
        Assert.Equal(3, reads);

        var writes = 0;
        gmail.On(HttpMethod.Post, "/messages/m1/trash", _ => { writes++; return Error(HttpStatusCode.TooManyRequests, "rateLimitExceeded"); });
        await Assert.ThrowsAsync<MailOperationException>(() => session.DeleteAsync(new MessageRef("INBOX", 0, 0, "m1"), false));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void The_token_is_only_ever_sent_to_gmail_googleapis_com()
    {
        Assert.StartsWith("https://gmail.googleapis.com/gmail/v1/users/me/", GmailClient.Url("/profile"));
        Assert.Throws<MailOperationException>(() => GmailClient.Url("https://evil.example/steal"));
        Assert.Throws<MailOperationException>(() => GmailClient.Url("//evil.example/x"));
    }
}
