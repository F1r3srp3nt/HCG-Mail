using System.Net;
using System.Text;
using System.Text.Json;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Addresses;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Mail;
using HCG.Mail.Core.Services;
using HCG.Mail.Email.Graph;
using HCG.Mail.Email.Html;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MessagePriority = HCG.Mail.Core.Compose.MessagePriority;

namespace HCG.Mail.Tests.Email;

/// <summary>Microsoft Graph mailbox workflow against a recording fake of graph.microsoft.com (no network, no real tokens).</summary>
public sealed class GraphMailSessionTests
{
    internal sealed class FakeAuth : IMicrosoftAuth
    {
        public int TokenVersion;
        public List<(string Key, bool Force)> Requests { get; } = [];
        public List<string> SignedOut { get; } = [];
        public MailOperationException? Failure { get; set; }
        public bool IsConfigured => true;
        public string? ConfigurationProblem => null;
        public IReadOnlyList<string> Scopes => ["User.Read", "Mail.ReadWrite", "Mail.Send"];

        public Task<MicrosoftSignIn> SignInAsync(string? loginHint, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetAccessTokenAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken)
        {
            Requests.Add((accountKey, forceRefresh));
            if (Failure is not null)
                throw Failure;
            if (forceRefresh)
                TokenVersion++;
            return Task.FromResult($"token-{accountKey}-v{TokenVersion}");
        }

        public Task SignOutAsync(string accountKey)
        {
            SignedOut.Add(accountKey);
            return Task.CompletedTask;
        }
    }

    internal sealed record Seen(HttpMethod Method, string Url, string? Body, string? ContentType, string? Authorization);

    internal sealed class FakeGraph : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = [];
        public List<Seen> Seen { get; } = [];

        public FakeGraph On(HttpMethod method, string pathPrefix, Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _routes.Add((r => r.Method == method && Uri.UnescapeDataString(r.RequestUri!.PathAndQuery).StartsWith("/v1.0" + pathPrefix, StringComparison.Ordinal), reply));
            return this;
        }

        public FakeGraph Json(HttpMethod method, string pathPrefix, object body, HttpStatusCode status = HttpStatusCode.OK) =>
            On(method, pathPrefix, _ => new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add(new(request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery), body, request.Content?.Headers.ContentType?.MediaType, request.Headers.Authorization?.ToString()));
            for (var i = _routes.Count - 1; i >= 0; i--)
            {
                if (_routes[i].Match(request))
                    return _routes[i].Reply(request);
            }
            return Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "no route");
        }
    }

    internal static HttpResponseMessage Error(HttpStatusCode status, string code, string message, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message } }), Encoding.UTF8, "application/json"),
        };
        if (retryAfter is { } wait)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return response;
    }

    internal static MailAccount Account(string key = "oid-1.tid-1") => new()
    {
        Id = "ms-" + key,
        DisplayName = "Kari Nordmann",
        EmailAddress = "kari@contoso.test",
        Username = "kari@contoso.test",
        Imap = MailAccount.GraphEndpoint,
        Smtp = MailAccount.GraphEndpoint,
        Provider = MailProviderKind.MicrosoftGraph,
        MicrosoftAccountId = key,
    };

    private readonly FakeAuth _auth = new();
    private readonly FakeGraph _graph = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly InMemoryDeliveryLog _log = new();

    private GraphMailSession Session(MailAccount? account = null) =>
        new(account ?? Account(), _auth, new HtmlToTextConverter(), NullLogger.Instance, _log, _graph, (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        });

    private static object Message(string id, string subject, bool read = false, string flag = "notFlagged") => new
    {
        id,
        subject,
        from = new { emailAddress = new { name = "Ola", address = "ola@example.test" } },
        toRecipients = new[] { new { emailAddress = new { name = "Kari", address = "kari@contoso.test" } } },
        receivedDateTime = "2026-11-12T08:30:00Z",
        bodyPreview = "Hei ‌ Kari https://track.example.test/x",
        isRead = read,
        flag = new { flagStatus = flag },
        hasAttachments = true,
        internetMessageId = "<abc@example.test>",
        isDraft = false,
        parentFolderId = "inbox-id",
    };

    private void WellKnownFolders(bool withArchive = true)
    {
        foreach (var (name, _) in GraphMailSession.WellKnownFolders)
        {
            if (name == "archive" && !withArchive)
                _graph.On(HttpMethod.Get, "/me/mailFolders/archive", _ => Error(HttpStatusCode.NotFound, "ErrorFolderNotFound", "none"));
            else
                _graph.Json(HttpMethod.Get, $"/me/mailFolders/{name}?", new { id = name + "-id" });
        }
    }

    [Fact]
    public async Task Folders_are_mapped_by_well_known_name_including_child_folders_and_paging()
    {
        WellKnownFolders(withArchive: false);
        _graph.Json(HttpMethod.Get, "/me/mailFolders?$top", new
        {
            value = new object[]
            {
                new { id = "inbox-id", displayName = "Innboks", childFolderCount = 1, unreadItemCount = 3, totalItemCount = 40 },
                new { id = "sentitems-id", displayName = "Sendte elementer", childFolderCount = 0, unreadItemCount = 0, totalItemCount = 9 },
            },
            @odata_nextLink = (string?)null,
        });
        _graph.Json(HttpMethod.Get, "/me/mailFolders/inbox-id/childFolders", new
        {
            value = new object[] { new { id = "proj-id", displayName = "Prosjekt", childFolderCount = 0, unreadItemCount = 1, totalItemCount = 2 } },
        });
        await using var session = Session();

        var folders = await session.GetFoldersAsync();

        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        Assert.Equal(("inbox-id", 3, 40), (inbox.FullName, inbox.UnreadCount!.Value, inbox.TotalCount!.Value));
        Assert.Equal(FolderRole.Sent, folders.Single(f => f.FullName == "sentitems-id").Role);
        var child = folders.Single(f => f.FullName == "proj-id");
        Assert.Equal((FolderRole.None, 1), (child.Role, child.Depth));
        Assert.DoesNotContain(folders, f => f.Role == FolderRole.Archive); // no archive folder: none invented
    }

    [Fact]
    public async Task Message_pages_use_server_paging_and_clean_previews()
    {
        _graph.Json(HttpMethod.Get, "/me/mailFolders/inbox-id?$select", new { totalItemCount = 120, unreadItemCount = 7 });
        _graph.Json(HttpMethod.Get, "/me/mailFolders/inbox-id/messages", new { value = new[] { Message("m1", "Hei", read: true, flag: "flagged"), Message("m2", "Andre") } });
        await using var session = Session();

        var page = await session.GetMessagesAsync("inbox-id", 50, 50);

        var request = _graph.Seen.Last().Url;
        Assert.Contains("$top=50", request);
        Assert.Contains("$skip=50", request);
        Assert.Contains("$orderby=receivedDateTime desc", request);
        Assert.Equal((120, 7, true), (page.TotalCount, page.UnreadCount, page.HasOlder));
        var first = page.Messages[0];
        Assert.Equal(new MessageRef("inbox-id", 0, 0, "m1"), first.Ref);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, first.Flags);
        Assert.Equal("Hei Kari", first.Preview);
        Assert.Equal("ola@example.test", first.From!.Address);
        Assert.Equal("abc@example.test", first.MessageId);
    }

    [Fact]
    public async Task Body_is_downloaded_as_mime_and_goes_through_the_shared_pipeline_with_inline_images_and_attachments()
    {
        var mime = await File.ReadAllBytesAsync(RenderingPipelineTests.Fixture("01-nested-mixed-alternative-related.eml"));
        _graph.On(HttpMethod.Get, "/me/messages/m1/$value", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mime) });
        _graph.Json(HttpMethod.Get, "/me/messages/m1?$select", new { isRead = true, flag = new { flagStatus = "notFlagged" }, isDraft = false });
        await using var session = Session();
        var reference = new MessageRef("inbox-id", 0, 0, "m1");

        var content = await session.GetMessageAsync(reference);
        Assert.Contains("Kvartalsrapport", content.HtmlBody);
        Assert.Single(content.InlineResources);
        Assert.Equal(MessageFlags.Seen, content.Flags);
        var bytes = await session.GetAttachmentBytesAsync(reference, 0);
        Assert.Equal("%PDF-1.4 synthetic fixture", Encoding.ASCII.GetString(bytes));
        Assert.Single(_graph.Seen, s => s.Url.EndsWith("/$value")); // cached: the attachment did not download the message again
    }

    [Fact]
    public async Task Flags_move_and_delete_use_the_right_requests()
    {
        WellKnownFolders();
        _graph.Json(HttpMethod.Get, "/me/mailFolders?$top", new { value = Array.Empty<object>() });
        _graph.On(HttpMethod.Patch, "/me/messages/", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        _graph.Json(HttpMethod.Post, "/me/messages/", new { id = "moved" }, HttpStatusCode.Created);
        _graph.On(HttpMethod.Delete, "/me/messages/", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var session = Session();
        await session.GetFoldersAsync();

        await session.SetFlagsAsync([new MessageRef("inbox-id", 0, 0, "m1")], MessageFlags.Seen | MessageFlags.Flagged, add: true);
        var patch = _graph.Seen.Single(s => s.Method == HttpMethod.Patch);
        Assert.Equal("{\"isRead\":true,\"flag\":{\"flagStatus\":\"flagged\"}}", patch.Body);

        await session.MoveAsync(new MessageRef("inbox-id", 0, 0, "m1"), "archive-id");
        Assert.Contains(_graph.Seen, s => s.Method == HttpMethod.Post && s.Url == "/v1.0/me/messages/m1/move" && s.Body == "{\"destinationId\":\"archive-id\"}");

        await session.DeleteAsync(new MessageRef("inbox-id", 0, 0, "m2"), permanent: false);
        Assert.Contains(_graph.Seen, s => s.Url == "/v1.0/me/messages/m2/move" && s.Body!.Contains("deleteditems"));

        await session.DeleteAsync(new MessageRef("deleteditems-id", 0, 0, "m3"), permanent: false); // already in Deleted Items
        Assert.Contains(_graph.Seen, s => s.Method == HttpMethod.Delete && s.Url == "/v1.0/me/messages/m3");
    }

    [Fact]
    public async Task Send_posts_mime_once_and_is_recorded_as_accepted_not_delivered()
    {
        _graph.On(HttpMethod.Post, "/me/sendMail", _ => new HttpResponseMessage(HttpStatusCode.Accepted));
        await using var session = Session();

        var result = await session.SendAsync(new OutgoingMessage
        {
            To = [new EmailAddress("Ola", "ola@example.test")],
            Bcc = [new EmailAddress(null, "hidden@example.test")],
            Subject = "Møte",
            TextBody = "Hei Ola",
            Priority = MessagePriority.High,
        }, saveCopyToSent: true);

        var send = Assert.Single(_graph.Seen);
        Assert.Equal("text/plain", send.ContentType);
        var mime = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(send.Body!)));
        Assert.Equal("kari@contoso.test", mime.From.Mailboxes.Single().Address);
        Assert.Equal("hidden@example.test", mime.Bcc.Mailboxes.Single().Address); // Exchange needs Bcc in the MIME and strips it on delivery
        Assert.Equal("high", mime.Headers[HeaderId.Importance]);
        Assert.Equal(DeliveryState.Accepted, result.Delivery!.State);
        Assert.Equal(202, result.Delivery.SmtpStatusCode);
        Assert.Single(_log.GetRecent());
    }

    [Fact]
    public async Task Throttled_send_is_not_retried_and_is_recorded_as_deferred()
    {
        _graph.On(HttpMethod.Post, "/me/sendMail", _ => Error(HttpStatusCode.TooManyRequests, "ApplicationThrottled", "slow down", TimeSpan.FromSeconds(3)));
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(new OutgoingMessage { To = [new EmailAddress(null, "ola@example.test")], Subject = "x", TextBody = "x" }, true));

        Assert.Equal(MailErrorKind.Throttled, ex.Kind);
        Assert.Contains("3 seconds", ex.UserMessage);
        Assert.Single(_graph.Seen);                 // never sent twice
        Assert.Empty(_waits);
        Assert.Equal(DeliveryState.Deferred, _log.GetRecent().Single().State);
    }

    [Fact]
    public async Task Lost_connection_during_send_is_recorded_as_unknown_not_as_not_sent()
    {
        _graph.On(HttpMethod.Post, "/me/sendMail", _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset"));
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.SendAsync(new OutgoingMessage { To = [new EmailAddress(null, "ola@example.test")], Subject = "x", TextBody = "x" }, true));

        Assert.True(ex.OutcomeUnknown);
        Assert.Equal(DeliveryState.Submitted, _log.GetRecent().Single().State);
        Assert.Single(_graph.Seen);
    }

    [Fact]
    public async Task Throttled_reads_are_retried_after_retry_after()
    {
        var calls = 0;
        _graph.On(HttpMethod.Get, "/me/mailFolders/inbox-id?$select", _ => ++calls == 1
            ? Error(HttpStatusCode.TooManyRequests, "ApplicationThrottled", "wait", TimeSpan.FromSeconds(2))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"totalItemCount\":0,\"unreadItemCount\":0}") });
        _graph.Json(HttpMethod.Get, "/me/mailFolders/inbox-id/messages", new { value = Array.Empty<object>() });
        await using var session = Session();

        var page = await session.GetMessagesAsync("inbox-id", 0, 50);

        Assert.Empty(page.Messages);
        Assert.Equal([TimeSpan.FromSeconds(2)], _waits);
    }

    [Fact]
    public async Task Draft_is_saved_as_mime_and_replaces_the_previous_draft()
    {
        _graph.Json(HttpMethod.Post, "/me/messages", new { id = "d2", parentFolderId = "drafts-id" }, HttpStatusCode.Created);
        _graph.On(HttpMethod.Delete, "/me/messages/d1", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var session = Session();

        var result = await session.SaveDraftAsync(new OutgoingMessage { Subject = "Utkast", TextBody = "x", DraftRef = new MessageRef("drafts-id", 0, 0, "d1") });

        Assert.Equal(new MessageRef("drafts-id", 0, 0, "d2"), result.DraftRef);
        Assert.Equal("text/plain", _graph.Seen[0].ContentType);
        Assert.Contains(_graph.Seen, s => s.Method == HttpMethod.Delete && s.Url == "/v1.0/me/messages/d1");
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_then_reconnect_is_required()
    {
        var calls = 0;
        _graph.On(HttpMethod.Get, "/me/mailFolders/inbox?", _ => ++calls == 1
            ? Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "expired")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"inbox-id\"}") });
        await using var session = Session();
        await session.ConnectAsync();
        Assert.Equal([false, true], _auth.Requests.Select(r => r.Force));
        Assert.Equal(MailConnectionState.Connected, session.State);

        _graph.On(HttpMethod.Get, "/me/mailFolders/inbox?", _ => Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "revoked"));
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
        Assert.Equal(MailErrorKind.ReconnectRequired, ex.Kind);
        Assert.Equal(MailConnectionState.Failed, session.State);
    }

    [Fact]
    public async Task Sign_in_that_can_no_longer_be_renewed_requires_reconnect()
    {
        _auth.Failure = new MailOperationException(MailErrorKind.ReconnectRequired, "Sign in again.");
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
        Assert.Equal(MailErrorKind.ReconnectRequired, ex.Kind);
        Assert.Empty(_graph.Seen); // nothing sent without a token
    }

    [Fact]
    public async Task Mailbox_without_graph_access_is_explained()
    {
        _graph.On(HttpMethod.Get, "/me/mailFolders/inbox?", _ => Error(HttpStatusCode.NotFound, "MailboxNotEnabledForRESTAPI", "REST API is not yet supported for this mailbox."));
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<MailOperationException>(() => session.ConnectAsync());
        Assert.Equal(MailErrorKind.NotSupported, ex.Kind);
        Assert.Contains("on-premises Exchange", ex.UserMessage);
    }

    [Fact]
    public async Task Accounts_are_isolated_each_uses_only_its_own_token()
    {
        _graph.Json(HttpMethod.Get, "/me/mailFolders/inbox?", new { id = "inbox-id" });
        await using var a = Session(Account("key-a"));
        await using var b = Session(Account("key-b"));
        await a.ConnectAsync();
        await b.ConnectAsync();
        Assert.Equal(["Bearer token-key-a-v0", "Bearer token-key-b-v0"], _graph.Seen.Select(s => s.Authorization));
    }

    [Fact]
    public void Token_is_never_sent_to_another_host()
    {
        Assert.Equal("https://graph.microsoft.com/v1.0/me", GraphClient.Url("https://graph.microsoft.com/v1.0/me"));
        var ex = Assert.Throws<MailOperationException>(() => GraphClient.Url("https://evil.example.test/v1.0/me"));
        Assert.Equal(MailErrorKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task Server_search_uses_graph_search_over_the_whole_folder()
    {
        _graph.Json(HttpMethod.Get, "/me/mailFolders/inbox-id/messages", new { value = new[] { Message("m9", "Faktura") } });
        await using var session = Session();
        var results = await session.SearchAsync("inbox-id", "faktura \"mai\"", 50);
        Assert.Equal("m9", results.Single().Ref.ServerId);
        Assert.Contains("$search=\"faktura  mai\"", _graph.Seen.Single().Url); // quotes inside the query are neutralised
    }

    [Fact]
    public void Imap_and_microsoft_accounts_get_their_own_session_type()
    {
        var factory = new MailSessionFactory(new HCG.Mail.Email.MailClientFactory(), new HtmlToTextConverter(), NullLoggerFactory.Instance, _auth);
        Assert.IsType<GraphMailSession>(factory.Create(Account(), ""));
        var imap = Account() with { Provider = MailProviderKind.ImapSmtp, MicrosoftAccountId = null };
        Assert.IsType<HCG.Mail.Email.Imap.MailKitMailSession>(factory.Create(imap, "pw"));
    }
}
