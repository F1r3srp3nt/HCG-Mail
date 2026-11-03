using System.Net;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using HCG.Mail.App.Preview;
using HCG.Mail.App.Services;
using HCG.Mail.App.ViewModels;
using HCG.Mail.App.Views;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Email.Deliverability;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HCG.Mail.App.Tests;

/// <summary>Mail Health window and the composer's priority selector, rendered headlessly with in-memory DNS.</summary>
public class MailHealthUiTests
{
    private sealed class StaticDns : IMailDns
    {
        public Dictionary<string, List<string>> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<IPAddress>> A { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<DnsAnswer<string>> GetTxtAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Txt.TryGetValue(name, out var r) ? DnsAnswer<string>.Found(r) : DnsAnswer<string>.NotFound());
        public Task<DnsAnswer<string>> GetMxAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult(DnsAnswer<string>.Found(["mx.example.com"]));
        public Task<DnsAnswer<IPAddress>> GetAddressesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(A.TryGetValue(name, out var r) ? DnsAnswer<IPAddress>.Found(r) : DnsAnswer<IPAddress>.NotFound());
        public Task<DnsAnswer<string>> GetPtrAsync(IPAddress address, CancellationToken ct = default) =>
            Task.FromResult(DnsAnswer<string>.NotFound());
    }

    private const string ReceivedHeaders = """
        Authentication-Results: mx.google.com;
               dkim=none (message not signed);
               spf=pass (google.com: domain of preview.user@example.com designates 192.0.2.10 as permitted sender) smtp.mailfrom=preview.user@example.com;
               dmarc=fail (p=NONE sp=NONE dis=NONE) header.from=example.com
        Received: from mail.example.com (mail.example.com [192.0.2.10]) by mx.google.com with ESMTPS id a1; Sun, 1 Nov 2026 10:00:01 -0800
        Received: from [192.168.1.20] (cm-1.example.net [203.0.113.50]) by smtp.example.com with ESMTPSA id z; Sun, 1 Nov 2026 19:00:00 +0100
        From: Preview User <preview.user@example.com>
        To: someone@gmail.com
        Subject: Deliverability test
        Date: Sun, 1 Nov 2026 19:00:00 +0100
        Message-Id: <T1@example.com>
        Importance: high
        X-Priority: 1
        Priority: urgent
        MIME-Version: 1.0
        """;

    [AvaloniaFact]
    public async Task Mail_health_runs_checks_labels_every_result_and_analyzes_headers()
    {
        var dns = new StaticDns();
        dns.Txt["example.com"] = ["v=spf1 ip4:192.0.2.0/24 ~all"];
        dns.Txt["_dmarc.example.com"] = ["v=DMARC1; p=none"];
        dns.A["smtp.example.com"] = [IPAddress.Parse("192.0.2.10")];
        var log = new InMemoryDeliveryLog();
        log.Add(new DeliveryRecord
        {
            Timestamp = DateTimeOffset.Now.AddMinutes(-5), AccountId = PreviewMailSession.SampleAccount.Id, AccountAddress = "preview.user@example.com",
            SmtpServer = "smtp.example.com:465", State = DeliveryState.Accepted, SmtpStatusCode = 250, ServerReply = "2.0.0 Ok: queued as 4QxYz1", RecipientCount = 1,
            Priority = MessagePriority.High, MessageId = "T1@example.com",
        });
        log.Add(new DeliveryRecord
        {
            Timestamp = DateTimeOffset.Now.AddMinutes(-2), AccountId = PreviewMailSession.SampleAccount.Id, AccountAddress = "preview.user@example.com",
            SmtpServer = "smtp.example.com:465", State = DeliveryState.Rejected, SmtpStatusCode = 550, ServerReply = "5.7.1 Message rejected due to content", RecipientCount = 2,
            ErrorMessage = "The outgoing server rejected the message: 5.7.1 Message rejected due to content",
        });

        await using var ui = await UiHarness.StartAsync(s =>
        {
            s.AddSingleton<IMailDns>(dns);
            s.AddSingleton<IDeliveryLog>(log);
        });
        var vm = ui.Services.GetRequiredService<MailHealthViewModelFactory>().Create(PreviewMailSession.SampleAccount);
        var window = new MailHealthWindow { DataContext = vm, Width = 1100, Height = 1500 };
        window.Show();

        await vm.RunChecksCommand.ExecuteAsync(null);
        await UiHarness.Settle();

        Assert.Equal(["Domain authentication", "SPF", "DKIM", "DMARC policy and alignment", "Sending server", "SMTP history", "Message format", "Sender reputation"],
            vm.Sections.Select(s => s.Title));
        var allowed = new[] { "Verified", "Warning", "Failed", "Not tested" };
        Assert.All(vm.Sections.SelectMany(s => s.Checks), c => Assert.Contains(c.StatusLabel, allowed));
        Assert.Equal(CheckStatus.Verified, vm.Sections.Single(s => s.Title == "SPF").Checks.First().Status);
        Assert.Equal(CheckStatus.NotTested, vm.Sections.Single(s => s.Title == "DKIM").Status);          // no selector known yet
        Assert.Equal(CheckStatus.Warning, vm.Sections.Single(s => s.Title == "DMARC policy and alignment").Status);
        Assert.Equal(CheckStatus.Failed, vm.Sections.Single(s => s.Title == "SMTP history").Status);      // the 550 rejection
        Assert.Equal(CheckStatus.Verified, vm.Sections.Single(s => s.Title == "Message format").Status);
        Assert.Equal(CheckStatus.NotTested, vm.Sections.Single(s => s.Title == "Sender reputation").Status);
        Assert.DoesNotContain(vm.Sections.SelectMany(s => s.Checks), c => c.Detail.Contains('%') && c.Title.Contains("score", StringComparison.OrdinalIgnoreCase));
        UiHarness.Capture(window, "mail-health-checks");

        vm.RawMessage = ReceivedHeaders;
        await vm.AnalyzeCommand.ExecuteAsync(null);
        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        await UiHarness.Settle();
        Assert.Contains(vm.AnalysisChecks, c => c.Title == "DKIM" && c.Status == CheckStatus.Failed);
        Assert.Contains(vm.AnalysisChecks, c => c.Title == "DMARC" && c.Status == CheckStatus.Failed);
        Assert.Contains(vm.AnalysisChecks, c => c.Title == "Conclusion" && c.Status == CheckStatus.Failed);
        UiHarness.Capture(window, "mail-health-analysis");

        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 2;
        await UiHarness.Settle();
        Assert.Equal(2, vm.History.Count);
        Assert.Contains("not that it reached the recipient", ((TextBlock)FindText(window, "not that it reached the recipient")!).Text);
        UiHarness.Capture(window, "mail-health-history");

        var report = vm.BuildReportText();
        Assert.Contains("[Failed]", report);
        Assert.Contains("No scores are estimated", report);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Composer_offers_low_normal_high_with_normal_default_and_explains_it()
    {
        await using var ui = await UiHarness.StartAsync();
        var compose = new ComposeViewModel(ui.ViewModel.Accounts, ui.ViewModel.Accounts[0], new OutgoingMessage(),
            ui.Services.GetRequiredService<AppSettingsService>(), Substitute.For<IDialogService>(), Substitute.For<INotificationService>());
        var window = new ComposeWindow { DataContext = compose };
        window.Show();
        await UiHarness.Settle();

        Assert.Equal(["Low", "Normal", "High"], ComposeViewModel.PriorityOptions.Select(o => o.Label));
        Assert.Equal(MessagePriority.Normal, compose.SelectedPriority.Value);
        Assert.Equal(MessagePriority.Normal, compose.BuildMessage(out _).Priority);

        var box = window.FindControl<ComboBox>("PriorityBox")!;
        box.SelectedIndex = 2;
        await UiHarness.Settle();
        Assert.Equal(MessagePriority.High, compose.BuildMessage(out _).Priority);
        Assert.True(compose.IsDirty);
        Assert.Contains("does not affect spam filtering", ComposeViewModel.PriorityExplanation);
        Assert.NotNull(FindText(window, "decide how priority is shown"));
        UiHarness.Capture(window, "compose-priority");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Draft_priority_is_restored_in_the_composer()
    {
        await using var ui = await UiHarness.StartAsync();
        var compose = new ComposeViewModel(ui.ViewModel.Accounts, ui.ViewModel.Accounts[0], new OutgoingMessage { Kind = ComposeKind.EditDraft, Priority = MessagePriority.Low },
            ui.Services.GetRequiredService<AppSettingsService>(), Substitute.For<IDialogService>(), Substitute.For<INotificationService>());
        Assert.Equal(MessagePriority.Low, compose.SelectedPriority.Value);
        Assert.False(compose.IsDirty);
    }

    [Fact]
    public void Send_confirmation_says_accepted_not_delivered()
    {
        var record = new DeliveryRecord
        {
            Timestamp = DateTimeOffset.Now, AccountId = "a", AccountAddress = "a@x.test", SmtpServer = "smtp.x.test:465",
            State = DeliveryState.Accepted, ServerReply = "2.0.0 Ok: queued as ABC",
        };
        var text = ComposeViewModel.DescribeAcceptance(new HCG.Mail.Core.Services.SendResult(true, null, record));
        Assert.Contains("Accepted by your outgoing server smtp.x.test:465 (2.0.0 Ok: queued as ABC)", text);
        Assert.Contains("not confirmed", text);
        Assert.DoesNotContain("delivered", text, StringComparison.OrdinalIgnoreCase);
    }

    private static TextBlock? FindText(Window window, string fragment) =>
        window.GetLogicalDescendantsOfType<TextBlock>().FirstOrDefault(t => t.Text?.Contains(fragment) == true);
}

internal static class LogicalTreeHelpers
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical root) where T : class
    {
        foreach (var child in root.LogicalChildren)
        {
            if (child is T match)
                yield return match;
            foreach (var nested in child.GetLogicalDescendantsOfType<T>())
                yield return nested;
        }
    }
}
