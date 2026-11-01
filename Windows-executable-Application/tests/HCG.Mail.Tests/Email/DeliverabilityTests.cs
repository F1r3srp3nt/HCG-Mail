using System.Net;
using System.Security.Cryptography;
using System.Text;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Email.Deliverability;
using HCG.Mail.Email.Mime;
using MimeKit;
using MimeKit.Cryptography;
using MessagePriority = HCG.Mail.Core.Compose.MessagePriority;

namespace HCG.Mail.Tests.Email;

public sealed class SpfTests
{
    private const string Domain = "hakmicapitalgroup.test";

    private static async Task<SpfResult> Check(FakeMailDns dns, string ip, string domain = Domain) =>
        (await new SpfEvaluator(dns).CheckHostAsync(IPAddress.Parse(ip), domain)).Result;

    [Fact]
    public void Parses_mechanisms_qualifiers_and_cidr()
    {
        var r = SpfRecord.Parse("v=spf1 ip4:192.0.2.0/24 ip6:2001:db8::/32 a mx:mail.example.test/28 include:_spf.provider.test ~all exp=explain.test");
        Assert.Empty(r.Errors);
        Assert.Equal(["ip4", "ip6", "a", "mx", "include", "all"], r.Mechanisms.Select(m => m.Name));
        Assert.Equal(24, r.Mechanisms[0].Cidr4);
        Assert.Equal(32, r.Mechanisms[1].Cidr6);
        Assert.Equal(28, r.Mechanisms[3].Cidr4);
        Assert.Equal('~', r.All!.Qualifier);
    }

    [Theory]
    [InlineData("v=spf1 ip4:300.1.1.1 -all")]
    [InlineData("v=spf1 ip4:192.0.2.1/33 -all")]
    [InlineData("v=spf1 include -all")]
    [InlineData("v=spf1 foo:bar -all")]
    [InlineData("v=spf1 all:example.test")]
    public void Invalid_terms_are_errors(string record) => Assert.NotEmpty(SpfRecord.Parse(record).Errors);

    [Fact]
    public async Task Ip4_include_and_all_are_evaluated_like_a_receiver()
    {
        var dns = new FakeMailDns()
            .AddTxt(Domain, "v=spf1 include:_spf.provider.test -all", "google-site-verification=abc")
            .AddTxt("_spf.provider.test", "v=spf1 ip4:198.51.100.0/24 ip6:2001:db8:1::/48 ~all");

        Assert.Equal(SpfResult.Pass, await Check(dns, "198.51.100.25"));
        Assert.Equal(SpfResult.Pass, await Check(dns, "2001:db8:1::5"));
        Assert.Equal(SpfResult.Fail, await Check(dns, "198.51.101.1"));   // not in the include → falls through to -all
    }

    [Fact]
    public async Task A_and_mx_mechanisms_use_dns()
    {
        var dns = new FakeMailDns().AddTxt(Domain, "v=spf1 a mx ~all").AddA(Domain, "192.0.2.10").AddA("mx1.provider.test", "192.0.2.20");
        dns.Mx[Domain] = ["mx1.provider.test"];
        Assert.Equal(SpfResult.Pass, await Check(dns, "192.0.2.10"));
        Assert.Equal(SpfResult.Pass, await Check(dns, "192.0.2.20"));
        Assert.Equal(SpfResult.SoftFail, await Check(dns, "192.0.2.30"));
    }

    [Fact]
    public async Task Redirect_is_followed()
    {
        var dns = new FakeMailDns().AddTxt(Domain, "v=spf1 redirect=_spf.provider.test").AddTxt("_spf.provider.test", "v=spf1 ip4:203.0.113.5 -all");
        Assert.Equal(SpfResult.Pass, await Check(dns, "203.0.113.5"));
        Assert.Equal(SpfResult.Fail, await Check(dns, "203.0.113.6"));
    }

    [Fact]
    public async Task No_record_two_records_and_dns_failure_are_distinguished()
    {
        Assert.Equal(SpfResult.None, await Check(new FakeMailDns(), "192.0.2.1"));
        Assert.Equal(SpfResult.PermError, await Check(new FakeMailDns().AddTxt(Domain, "v=spf1 -all", "v=spf1 ~all"), "192.0.2.1"));
        var broken = new FakeMailDns();
        broken.Broken.Add(Domain);
        Assert.Equal(SpfResult.TempError, await Check(broken, "192.0.2.1"));
    }

    [Fact]
    public async Task More_than_ten_lookups_is_a_permerror()
    {
        var dns = new FakeMailDns();
        var includes = string.Join(' ', Enumerable.Range(1, 11).Select(i => $"include:s{i}.test"));
        dns.AddTxt(Domain, $"v=spf1 {includes} -all");
        for (var i = 1; i <= 11; i++)
            dns.AddTxt($"s{i}.test", "v=spf1 ip4:10.0.0.1 -all");

        Assert.Equal(SpfResult.PermError, await Check(dns, "192.0.2.1"));
        var (count, _) = await new SpfEvaluator(dns).CountLookupsAsync(Domain);
        Assert.Equal(11, count);
    }

    [Fact]
    public async Task Macros_and_ptr_are_not_guessed()
    {
        Assert.Equal(SpfResult.NotEvaluated, await Check(new FakeMailDns().AddTxt(Domain, "v=spf1 exists:%{i}.spf.test -all"), "192.0.2.1"));
        Assert.Equal(SpfResult.NotEvaluated, await Check(new FakeMailDns().AddTxt(Domain, "v=spf1 ptr -all"), "192.0.2.1"));
    }

    [Theory]
    [InlineData("192.0.2.255", "192.0.2.0", 24, true)]
    [InlineData("192.0.3.0", "192.0.2.0", 24, false)]
    [InlineData("192.0.2.7", "192.0.2.0", 29, true)]
    [InlineData("192.0.2.8", "192.0.2.0", 29, false)]
    [InlineData("2001:db8::ffff", "2001:db8::", 64, true)]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1", 32, true)]
    public void Cidr_matching(string ip, string network, int prefix, bool expected) =>
        Assert.Equal(expected, SpfEvaluator.InRange(IPAddress.Parse(ip), IPAddress.Parse(network), prefix));
}

public sealed class PolicyRecordTests
{
    [Fact]
    public void Dmarc_record_is_parsed()
    {
        var r = DmarcRecord.TryParse("v=DMARC1; p=quarantine; sp=reject; pct=50; adkim=s; rua=mailto:a@x.test,mailto:b@x.test", out var error);
        Assert.Null(error);
        Assert.Equal("quarantine", r!.Policy);
        Assert.Equal("reject", r.SubdomainPolicy);
        Assert.Equal(50, r.Percent);
        Assert.Equal('s', r.DkimAlignment);
        Assert.Equal('r', r.SpfAlignment);
        Assert.Equal(2, r.AggregateReports.Count);
    }

    [Theory]
    [InlineData("v=DMARC1; rua=mailto:a@x.test")]
    [InlineData("p=none; v=DMARC1")]
    [InlineData("v=DMARC1; p=maybe")]
    public void Invalid_dmarc_records_are_rejected(string txt) => Assert.Null(DmarcRecord.TryParse(txt, out _));

    [Theory]
    [InlineData(2048)]
    [InlineData(1024)]
    public void Dkim_rsa_key_size_is_read(int bits)
    {
        using var rsa = RSA.Create(bits);
        var key = DkimKeyRecord.Parse($"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}");
        Assert.Null(key.Error);
        Assert.Equal(bits, key.KeyBits);
    }

    [Fact]
    public void Dkim_revoked_testing_and_broken_keys()
    {
        Assert.True(DkimKeyRecord.Parse("v=DKIM1; p=").Revoked);
        Assert.True(DkimKeyRecord.Parse("v=DKIM1; t=y; p=" + Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo())).Testing);
        Assert.NotNull(DkimKeyRecord.Parse("v=DKIM1; p=!!!").Error);
        Assert.NotNull(DkimKeyRecord.Parse("v=DKIM2; p=AAAA").Error);
        Assert.Equal(256, DkimKeyRecord.Parse("v=DKIM1; k=ed25519; p=" + Convert.ToBase64String(new byte[32])).KeyBits);
    }

    [Theory]
    [InlineData("mail.example.com", "example.com", 'r', true)]
    [InlineData("mail.example.com", "example.com", 's', false)]
    [InlineData("example.com", "example.com", 's', true)]
    [InlineData("domene.no", "hakmicapitalgroup.com", 'r', false)]
    public void Alignment(string authenticated, string from, char mode, bool aligned) =>
        Assert.Equal(aligned, DomainNames.Aligned(authenticated, from, mode));
}

public sealed class DomainAuthenticationCheckerTests
{
    private const string Domain = "hakmicapitalgroup.test";

    [Fact]
    public async Task Missing_records_are_failed_with_actions_and_dns_errors_are_not_tested()
    {
        var checker = new DomainAuthenticationChecker(new FakeMailDns());
        var spf = await checker.CheckSpfAsync(Domain);
        Assert.Equal(CheckStatus.Failed, spf.Single().Status);
        Assert.Contains("Domene.no", spf.Single().Action);
        Assert.DoesNotContain("include:_spf", spf.Single().Action); // never invents a provider value

        var (dmarc, record) = await checker.CheckDmarcAsync(Domain);
        Assert.Null(record);
        Assert.Equal(CheckStatus.Failed, dmarc.Single().Status);
        Assert.Contains("_dmarc." + Domain, dmarc.Single().Action);

        var broken = new FakeMailDns();
        broken.Broken.Add(Domain);
        Assert.Equal(CheckStatus.NotTested, (await new DomainAuthenticationChecker(broken).CheckSpfAsync(Domain)).Single().Status);
    }

    [Fact]
    public async Task Healthy_domain_is_verified()
    {
        using var rsa = RSA.Create(2048);
        var dns = new FakeMailDns()
            .AddTxt(Domain, "v=spf1 include:_spf.provider.test -all")
            .AddTxt("_spf.provider.test", "v=spf1 ip4:198.51.100.0/24 -all")
            .AddTxt("_dmarc." + Domain, "v=DMARC1; p=quarantine; rua=mailto:dmarc@" + Domain)
            .AddTxt("sel1._domainkey." + Domain, "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()))
            .AddA("smtp.provider.test", "198.51.100.7")
            .AddA("out7.provider.test", "198.51.100.7");
        dns.Ptr["198.51.100.7"] = ["out7.provider.test"];
        dns.Mx[Domain] = ["mx.provider.test"];
        var checker = new DomainAuthenticationChecker(dns);

        Assert.All(await checker.CheckSpfAsync(Domain), c => Assert.Equal(CheckStatus.Verified, c.Status));
        Assert.All(await checker.CheckDkimAsync(Domain, ["sel1"]), c => Assert.Equal(CheckStatus.Verified, c.Status));
        Assert.All((await checker.CheckDmarcAsync(Domain)).Results, c => Assert.Equal(CheckStatus.Verified, c.Status));
        Assert.All(await checker.CheckSendingServerAsync(Domain, "smtp.provider.test"), c => Assert.Equal(CheckStatus.Verified, c.Status));
    }

    [Fact]
    public async Task Weak_settings_are_warnings_or_failures()
    {
        using var rsa = RSA.Create(1024);
        var dns = new FakeMailDns()
            .AddTxt(Domain, "v=spf1 +all")
            .AddTxt("_dmarc." + Domain, "v=DMARC1; p=none")
            .AddTxt("old._domainkey." + Domain, "v=DKIM1; p=")
            .AddTxt("small._domainkey." + Domain, "v=DKIM1; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        var checker = new DomainAuthenticationChecker(dns);

        Assert.Contains(await checker.CheckSpfAsync(Domain), c => c.Title == "SPF default policy" && c.Status == CheckStatus.Failed);
        var dmarc = (await checker.CheckDmarcAsync(Domain)).Results;
        Assert.Contains(dmarc, c => c.Title == "DMARC policy" && c.Status == CheckStatus.Warning);
        Assert.Contains(dmarc, c => c.Title == "DMARC reports" && c.Status == CheckStatus.Warning);
        var dkim = await checker.CheckDkimAsync(Domain, ["old", "small", "missing"]);
        Assert.Equal([CheckStatus.Failed, CheckStatus.Warning, CheckStatus.Failed], dkim.Select(c => c.Status));
        Assert.Equal(CheckStatus.NotTested, (await checker.CheckDkimAsync(Domain, [])).Single().Status);
    }

    [Fact]
    public async Task Outgoing_server_not_in_spf_is_a_warning_not_a_failure()
    {
        var dns = new FakeMailDns().AddTxt(Domain, "v=spf1 ip4:198.51.100.1 -all").AddA("smtp.provider.test", "203.0.113.9");
        var results = await new DomainAuthenticationChecker(dns).CheckSendingServerAsync(Domain, "smtp.provider.test");
        var spf = results.Single(c => c.Title == "SPF authorizes the outgoing server");
        Assert.Equal(CheckStatus.Warning, spf.Status);                           // providers relay through other servers
        Assert.Contains(results, c => c.Title.StartsWith("Reverse DNS") && c.Status == CheckStatus.Warning);
    }
}

public sealed class ReceivedMessageAnalyzerTests
{
    private const string From = "post@hakmicapitalgroup.test";

    private static string Headers(string authResults, string dkimSignature = "", string firstHop = "[192.168.1.20]", string extra = "") =>
        $"""
        Delivered-To: ola@gmail.test
        Received: by 2002:a05:6000:1 with SMTP id x; Sun, 1 Nov 2026 10:00:02 -0800 (PST)
        Authentication-Results: mx.google.test;
               dkim=pass header.i=@hakmicapitalgroup.test header.s=sel1 header.b=abc;
               spf=pass (google.test: domain of post@hakmicapitalgroup.test designates 198.51.100.7 as permitted sender) smtp.mailfrom=post@hakmicapitalgroup.test;
               dmarc=pass (p=QUARANTINE sp=QUARANTINE dis=NONE) header.from=hakmicapitalgroup.test
        Authentication-Results: forged.example; dkim=pass header.d=evil.test
        Return-Path: <post@hakmicapitalgroup.test>
        Received: from out7.provider.test (out7.provider.test [198.51.100.7]) by mx.google.test with ESMTPS id y; Sun, 1 Nov 2026 10:00:01 -0800
        Received: from {firstHop} (cm-1.example.net [203.0.113.50]) by smtp.provider.test with ESMTPSA id z; Sun, 1 Nov 2026 19:00:00 +0100
        {dkimSignature}From: Yasin <{From}>
        To: ola@gmail.test
        Subject: Test
        Date: Sun, 1 Nov 2026 19:00:00 +0100
        Message-Id: <ABC123@hakmicapitalgroup.test>
        MIME-Version: 1.0
        {extra}Content-Type: text/plain; charset=utf-8
        """.Replace("mx.google.test;\n", "mx.google.test;\n").Replace(
            "dkim=pass header.i=@hakmicapitalgroup.test header.s=sel1 header.b=abc;\n       spf=pass", authResults == "" ? "dkim=pass header.i=@hakmicapitalgroup.test header.s=sel1 header.b=abc;\n       spf=pass" : authResults);

    [Fact]
    public async Task All_pass_is_verified_and_points_at_reputation_not_authentication()
    {
        var analysis = await new ReceivedMessageAnalyzer(new FakeMailDns()).AnalyzeTextAsync(Headers(""));
        Assert.Equal("mx.google.test", analysis.ReportedBy);
        Assert.Equal(CheckStatus.Verified, analysis.Result("SPF"));
        Assert.Equal(CheckStatus.Verified, analysis.Result("DKIM"));
        Assert.Equal(CheckStatus.Verified, analysis.Result("DMARC"));
        Assert.Equal(CheckStatus.Verified, analysis.Result("SPF alignment"));
        var conclusion = analysis.Checks.Single(c => c.Title == "Conclusion");
        Assert.Equal(CheckStatus.Verified, conclusion.Status);
        Assert.Contains("reputation", conclusion.Detail);
        Assert.Contains(analysis.Checks, c => c.Title.Contains("Recipient's authentication results") && c.Detail.Contains("1 older header"));
        Assert.Equal(CheckStatus.Verified, analysis.Result("First hop EHLO name"));
    }

    [Fact]
    public async Task Unsigned_mail_with_failing_dmarc_is_explained()
    {
        var raw = Headers("dkim=none (message not signed);\n       spf=softfail").Replace("dmarc=pass (p=QUARANTINE sp=QUARANTINE dis=NONE)", "dmarc=fail (p=NONE dis=NONE)");
        var analysis = await new ReceivedMessageAnalyzer(new FakeMailDns()).AnalyzeTextAsync(raw);
        Assert.Equal(CheckStatus.Failed, analysis.Result("DKIM"));
        Assert.Equal(CheckStatus.Warning, analysis.Result("SPF"));
        Assert.Equal(CheckStatus.Failed, analysis.Result("DMARC"));
        Assert.Equal(CheckStatus.Failed, analysis.Result("DKIM signature"));
        Assert.Equal(CheckStatus.Failed, analysis.Checks.Single(c => c.Title == "Conclusion").Status);
        Assert.Contains("Domene.no", analysis.Checks.Single(c => c.Title == "DKIM").Action);
    }

    [Fact]
    public async Task Provider_domain_signature_does_not_align()
    {
        var raw = Headers("dkim=pass header.d=domene.test header.s=k1 header.b=x;\n       spf=pass",
            dkimSignature: "DKIM-Signature: v=1; a=rsa-sha256; d=domene.test; s=k1; h=from; bh=AAAA; b=AAAA\n");
        var analysis = await new ReceivedMessageAnalyzer(new FakeMailDns()).AnalyzeTextAsync(raw);
        var alignment = analysis.Checks.Single(c => c.Title == "DKIM alignment");
        Assert.Equal(CheckStatus.Failed, alignment.Status);
        Assert.Contains("domene.test", alignment.Detail);
        Assert.Contains(analysis.Checks, c => c.Title.StartsWith("DKIM signature d=domene.test") && c.Status == CheckStatus.NotTested); // headers only
    }

    [Fact]
    public async Task Hostname_ehlo_and_spam_verdicts_are_reported_verbatim()
    {
        var raw = Headers("", firstHop: "Yasins-MacBook-Pro", extra: "X-Spam-Flag: YES\nX-Spam-Status: Yes, score=6.1 required=5.0\nX-Forefront-Antispam-Report: CIP:1.2.3.4;SCL:1;SFV:NSPM\n");
        var analysis = await new ReceivedMessageAnalyzer(new FakeMailDns()).AnalyzeTextAsync(raw);
        Assert.Equal(CheckStatus.Warning, analysis.Result("First hop EHLO name"));
        Assert.Equal(CheckStatus.Warning, analysis.Result("Spam filter: X-Spam-Flag"));
        Assert.Contains("score=6.1", analysis.Checks.Single(c => c.Title == "Spam filter: X-Spam-Status").Detail);
        Assert.Equal(CheckStatus.NotTested, analysis.Result("Spam filter: X-Forefront-Antispam-Report"));
    }

    [Fact]
    public async Task Text_without_headers_is_rejected_clearly()
    {
        var analysis = await new ReceivedMessageAnalyzer(new FakeMailDns()).AnalyzeTextAsync("Hei Ola, takk for sist!");
        Assert.Equal(CheckStatus.Failed, analysis.Checks.Single().Status);
    }

    [Fact]
    public async Task Dkim_signature_is_verified_independently_and_tampering_is_detected()
    {
        using var rsa = RSA.Create(2048);
        var dns = new FakeMailDns().AddTxt("sel1._domainkey.hakmicapitalgroup.test", "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Yasin", From));
        message.To.Add(new MailboxAddress("Ola", "ola@gmail.test"));
        message.Subject = "Signert";
        message.Date = DateTimeOffset.Now;
        message.MessageId = "sig1@hakmicapitalgroup.test";
        message.Body = new TextPart("plain") { Text = "Hei Ola,\nmøtet er flyttet.\n" };
        message.Prepare(EncodingConstraint.SevenBit);
        using var pem = new MemoryStream(Encoding.ASCII.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        var signer = new DkimSigner(pem, "hakmicapitalgroup.test", "sel1")
        {
            HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        };
        signer.Sign(message, [HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId]);
        var raw = MessageFormatValidator.SerializeForTransport(message);

        var good = await new ReceivedMessageAnalyzer(dns).AnalyzeAsync(raw);
        Assert.Equal(CheckStatus.Verified, good.Signatures.Single().Verification);
        Assert.Equal(["sel1"], good.OwnDomainSelectors);
        Assert.Equal(CheckStatus.Verified, good.Result("DKIM alignment"));

        var tampered = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(raw).Replace("flyttet", "avlyst!"));
        var bad = await new ReceivedMessageAnalyzer(dns).AnalyzeAsync(tampered);
        Assert.Equal(CheckStatus.Warning, bad.Signatures.Single().Verification);

        var noKey = await new ReceivedMessageAnalyzer(new FakeMailDns()).AnalyzeAsync(raw);
        Assert.NotEqual(CheckStatus.Verified, noKey.Signatures.Single().Verification);
    }
}

public sealed class MessageFormatTests
{
    private static readonly MailAccount Account = new()
    {
        Id = "a",
        DisplayName = "Yasin Hakmi",
        EmailAddress = "post@hakmicapitalgroup.test",
        Username = "post@hakmicapitalgroup.test",
        Imap = new ServerEndpoint("mail.provider.test", 993, MailSecurityMode.SslTls),
        Smtp = new ServerEndpoint("mail.provider.test", 465, MailSecurityMode.SslTls),
    };

    [Theory]
    [InlineData(MessagePriority.Low)]
    [InlineData(MessagePriority.Normal)]
    [InlineData(MessagePriority.High)]
    public void Hcg_mail_output_passes_every_format_check(MessagePriority priority)
    {
        var results = MessageFormatValidator.ValidateOwnOutput(Account, priority);
        Assert.All(results, r => Assert.True(r.Status == CheckStatus.Verified, $"{r.Title}: {r.Status} — {r.Detail}"));
    }

    private static IReadOnlyList<CheckResult> ValidateRaw(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        return MessageFormatValidator.Validate(MimeMessage.Load(new MemoryStream(raw)), raw);
    }

    private const string Good = "From: a@x.test\r\nTo: b@y.test\r\nSubject: s\r\nDate: Sun, 1 Nov 2026 10:00:00 +0100\r\nMessage-ID: <1@x.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain\r\n\r\nbody\r\n";

    [Fact]
    public void Defects_are_detected()
    {
        Assert.All(ValidateRaw(Good), r => Assert.Equal(CheckStatus.Verified, r.Status));
        Assert.Equal(CheckStatus.Failed, ValidateRaw(Good.Replace("Date: Sun, 1 Nov 2026 10:00:00 +0100\r\n", "")).Single(r => r.Title == "Date").Status);
        Assert.Equal(CheckStatus.Failed, ValidateRaw(Good.Replace("Message-ID: <1@x.test>\r\n", "")).Single(r => r.Title == "Message-ID").Status);
        Assert.Equal(CheckStatus.Warning, ValidateRaw(Good.Replace("<1@x.test>", "<1@other.test>")).Single(r => r.Title == "Message-ID").Status);
        Assert.Equal(CheckStatus.Failed, ValidateRaw(Good.Replace("Subject: s\r\n", "Subject: s\r\nSubject: t\r\n")).Single(r => r.Title == "Header fields").Status);
        Assert.Equal(CheckStatus.Failed, ValidateRaw(Good.Replace("To: b@y.test\r\n", "To: b@y.test\r\nBcc: hidden@z.test\r\n")).Single(r => r.Title == "Bcc").Status);
        Assert.Equal(CheckStatus.Failed, ValidateRaw(Good.Replace("body", new string('x', 1200))).Single(r => r.Title == "Line length").Status);
        Assert.Equal(CheckStatus.Failed, ValidateRaw(Good.Replace("body\r\n", "body\nmore\r\n")).Single(r => r.Title == "Line endings").Status);
        Assert.Equal(CheckStatus.Warning, ValidateRaw(Good.Replace("body", "møte")).Single(r => r.Title == "Transfer encoding").Status);
        Assert.Equal(CheckStatus.Warning, ValidateRaw(Good.Replace("MIME-Version", "Importance: high\r\nX-Priority: 5\r\nMIME-Version")).Single(r => r.Title == "Priority headers").Status);
        Assert.Equal(CheckStatus.Warning, ValidateRaw(Good.Replace("Subject: s", "Subject: ")).Single(r => r.Title == "Subject").Status);
    }
}

public sealed class SendingHistoryTests
{
    private static DeliveryRecord Record(DeliveryState state, int? code = null, string? reply = null) => new()
    {
        Timestamp = DateTimeOffset.Now,
        AccountId = "a",
        AccountAddress = "post@x.test",
        SmtpServer = "smtp.x.test:465",
        State = state,
        SmtpStatusCode = code,
        ServerReply = reply,
    };

    [Fact]
    public void Empty_history_is_not_tested_and_accepted_is_never_called_delivered()
    {
        Assert.Equal(CheckStatus.NotTested, MailHealthService.CheckHistory([]).Single().Status);
        var ok = MailHealthService.CheckHistory([Record(DeliveryState.Accepted, 250, "2.0.0 Ok: queued as X")]).Single();
        Assert.Equal(CheckStatus.Verified, ok.Status);
        Assert.Contains("not known", ok.Detail);
        Assert.DoesNotContain("delivered to", ok.Detail);
    }

    [Fact]
    public void Rejections_are_listed_with_the_server_reply_and_meaning()
    {
        var results = MailHealthService.CheckHistory([Record(DeliveryState.Rejected, 550, "5.7.1 Message rejected as spam"), Record(DeliveryState.Deferred, 451, "4.7.1 Try again later")]);
        Assert.Equal(CheckStatus.Warning, results[0].Status);
        var rejected = results.Single(r => r.Status == CheckStatus.Failed);
        Assert.Contains("5.7.1 Message rejected as spam", rejected.Detail);
        Assert.Contains("policy", rejected.Action);
        Assert.Contains("Temporarily", results.Single(r => r.Title.Contains("Deferred")).Action);
    }

    [Theory]
    [InlineData(550, "5.7.26 Unauthenticated email from x is not accepted", "SPF/DKIM/DMARC")]
    [InlineData(535, "5.7.8 Authentication credentials invalid", "password")]
    [InlineData(552, "5.3.4 Message size exceeds fixed limit", "too large")]
    public void Known_status_codes_are_explained(int code, string reply, string expected) =>
        Assert.Contains(expected, MailHealthService.ExplainReply(code, reply));
}
