using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Email.Deliverability;

namespace HCG.Mail.App.ViewModels;

public sealed class CheckItemViewModel(CheckResult result)
{
    public string Title => result.Title;
    public CheckStatus Status => result.Status;
    public string StatusLabel => result.StatusLabel;
    public string Detail => result.Detail;
    public string? Action => result.Action;
    public bool HasAction => !string.IsNullOrEmpty(result.Action);
    public bool IsVerified => Status == CheckStatus.Verified;
    public bool IsWarning => Status == CheckStatus.Warning;
    public bool IsFailed => Status == CheckStatus.Failed;
    public bool IsNotTested => Status == CheckStatus.NotTested;
}

public sealed class CheckSectionViewModel(CheckSection section)
{
    public string Title => section.Title;
    public string Description => section.Description;
    public CheckStatus Status => section.Summary;
    public string StatusLabel => CheckResult.Label(section.Summary);
    public bool IsVerified => Status == CheckStatus.Verified;
    public bool IsWarning => Status == CheckStatus.Warning;
    public bool IsFailed => Status == CheckStatus.Failed;
    public bool IsNotTested => Status == CheckStatus.NotTested;
    public IReadOnlyList<CheckItemViewModel> Checks { get; } = section.Checks.Select(c => new CheckItemViewModel(c)).ToList();
}

public sealed class DeliveryRecordViewModel(DeliveryRecord record)
{
    public string Time => record.Timestamp.LocalDateTime.ToString("g");
    public string State => record.StateLabel;
    public bool IsFailure => record.IsFailure;
    public string Reply => record.ServerReply is null ? "No reply text" : $"{record.SmtpStatusCode} {record.ServerReply}".Trim();
    public string Details => $"{record.RecipientCount} recipient(s) · priority {record.Priority.ToString().ToLowerInvariant()} · via {record.SmtpServer}" +
                             (record.MessageId is null ? "" : $" · Message-ID <{record.MessageId}>");
    public string Explanation => record.ErrorMessage ?? record.Explanation;
}

/// <summary>
/// Mail Health for one account: DNS authentication checks, the account's SMTP history, message-format validation,
/// analysis of messages as received by someone else, and reputation resources. Read-only: HCG Mail never changes DNS
/// or provider settings, and never shows scores it cannot measure.
/// </summary>
public sealed partial class MailHealthViewModel : ObservableObject
{
    private readonly MailAccount _account;
    private readonly MailHealthService _health;
    private readonly ReceivedMessageAnalyzer _analyzer;
    private readonly IDeliveryLog _deliveryLog;
    private readonly ILinkOpener _links;
    private readonly IDialogService _dialogs;
    private MailHealthReport? _report;
    private ReceivedMessageAnalysis? _analysis;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunChecksCommand), nameof(AnalyzeCommand), nameof(OpenMessageFileCommand))] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Checks have not run yet. They query public DNS only; nothing is sent and nothing is changed.";
    [ObservableProperty] private string _dkimSelectors = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))] private string _rawMessage = "";
    [ObservableProperty] private string? _analysisSummary;

    public MailHealthViewModel(MailAccount account, MailHealthService health, ReceivedMessageAnalyzer analyzer, IDeliveryLog deliveryLog, ILinkOpener links, IDialogService dialogs)
    {
        _account = account;
        _health = health;
        _analyzer = analyzer;
        _deliveryLog = deliveryLog;
        _links = links;
        _dialogs = dialogs;
        LoadHistory();
    }

    public string AccountAddress => _account.EmailAddress;
    public string Domain => DomainNames.DomainOf(_account.EmailAddress) ?? _account.EmailAddress;
    public string WindowTitle => $"Mail Health — {AccountAddress}";

    public ObservableCollection<CheckSectionViewModel> Sections { get; } = [];
    public ObservableCollection<CheckItemViewModel> AnalysisChecks { get; } = [];
    public ObservableCollection<string> AnalysisHops { get; } = [];
    public ObservableCollection<DeliveryRecordViewModel> History { get; } = [];
    public IReadOnlyList<ReputationLink> ReputationLinks => MailHealthService.ReputationLinks;

    public bool HasHistory => History.Count > 0;
    public bool HasAnalysis => AnalysisChecks.Count > 0;
    public string DeliveryConfirmationText => DeliveryRecord.DeliveryConfirmationText;
    public string InboxPlacementText => DeliveryRecord.InboxPlacementText;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunChecksAsync()
    {
        IsBusy = true;
        StatusText = $"Checking {Domain}…";
        try
        {
            LoadHistory();
            _report = await _health.RunAsync(_account, ParseSelectors(), _analysis, _deliveryLog.GetRecent(_account.Id, 50));
            Sections.Clear();
            foreach (var section in _report.Sections)
                Sections.Add(new CheckSectionViewModel(section));
            StatusText = $"Checked {_report.CheckedAt.LocalDateTime:g}. Results reflect public DNS and this computer's send history; HCG Mail cannot change DNS or your provider's servers.";
        }
        catch (Exception ex)
        {
            StatusText = "The checks could not complete: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAnalyze() => !IsBusy && !string.IsNullOrWhiteSpace(RawMessage);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync() => await RunAnalysisAsync(() => _analyzer.AnalyzeTextAsync(RawMessage, _report?.Dmarc));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task OpenMessageFileAsync()
    {
        var files = await _dialogs.PickFilesAsync("Open a saved message (.eml)");
        if (files.Count == 0)
            return;
        var info = new FileInfo(files[0]);
        if (info.Length > 30 * 1024 * 1024)
        {
            AnalysisSummary = "That file is larger than 30 MB; save the message without large attachments, or paste its headers instead.";
            return;
        }
        var bytes = await File.ReadAllBytesAsync(info.FullName);
        RawMessage = $"(opened {info.Name})";
        await RunAnalysisAsync(() => _analyzer.AnalyzeAsync(bytes, _report?.Dmarc));
    }

    private async Task RunAnalysisAsync(Func<Task<ReceivedMessageAnalysis>> analyze)
    {
        IsBusy = true;
        AnalysisSummary = "Analyzing…";
        try
        {
            _analysis = await analyze();
            AnalysisChecks.Clear();
            foreach (var check in _analysis.Checks)
                AnalysisChecks.Add(new CheckItemViewModel(check));
            AnalysisHops.Clear();
            foreach (var hop in _analysis.Hops)
                AnalysisHops.Add(hop);

            var summary = new StringBuilder();
            if (_analysis.FromAddress is not null)
                summary.Append($"From {_analysis.FromAddress}");
            if (_analysis.ReportedBy is not null)
                summary.Append($" · results recorded by {_analysis.ReportedBy}");
            if (!_analysis.HasBody)
                summary.Append(" · headers only (DKIM not independently verified)");
            if (_analysis.FromDomain is not null && !_analysis.FromDomain.Equals(Domain, StringComparison.OrdinalIgnoreCase))
                summary.Append($" · note: this message is from {_analysis.FromDomain}, not {Domain}");
            if (_analysis.OwnDomainSelectors.Count > 0)
            {
                var merged = ParseSelectors().Concat(_analysis.OwnDomainSelectors).Distinct(StringComparer.OrdinalIgnoreCase);
                DkimSelectors = string.Join(", ", merged);
                summary.Append($" · DKIM selector(s) found: {string.Join(", ", _analysis.OwnDomainSelectors)} — run the domain checks again to verify the published key");
            }
            AnalysisSummary = summary.Length > 0 ? summary.ToString() : null;
            OnPropertyChanged(nameof(HasAnalysis));
        }
        catch (Exception ex)
        {
            AnalysisSummary = "The message could not be analyzed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearAnalysis()
    {
        _analysis = null;
        RawMessage = "";
        AnalysisSummary = null;
        AnalysisChecks.Clear();
        AnalysisHops.Clear();
        OnPropertyChanged(nameof(HasAnalysis));
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear send history?", $"The SMTP history for {AccountAddress} on this computer will be deleted. Sent messages are not affected.", "Clear", destructive: true))
            return;
        _deliveryLog.Clear(_account.Id);
        LoadHistory();
    }

    [RelayCommand]
    private Task OpenLinkAsync(ReputationLink link) => _links.OpenAsync(link.Url);

    /// <summary>Plain-text report for sending to the mail provider's support.</summary>
    public string BuildReportText()
    {
        var text = new StringBuilder();
        text.AppendLine($"HCG Mail — Mail Health report for {AccountAddress} ({Domain})");
        text.AppendLine($"Outgoing server: {_account.Smtp.Host}:{_account.Smtp.Port}");
        if (_report is not null)
            text.AppendLine($"Checked: {_report.CheckedAt:u}");
        text.AppendLine("Status labels: Verified = confirmed; Warning; Failed; Not tested = could not be determined. No scores are estimated.");
        foreach (var section in Sections)
        {
            text.AppendLine().AppendLine($"== {section.Title}: {section.StatusLabel} ==");
            foreach (var check in section.Checks)
            {
                text.AppendLine($"[{check.StatusLabel}] {check.Title}: {check.Detail}");
                if (check.HasAction)
                    text.AppendLine($"    Action: {check.Action}");
            }
        }
        if (AnalysisChecks.Count > 0)
        {
            text.AppendLine().AppendLine("== Received message analysis ==");
            if (AnalysisSummary is not null)
                text.AppendLine(AnalysisSummary);
            foreach (var check in AnalysisChecks)
                text.AppendLine($"[{check.StatusLabel}] {check.Title}: {check.Detail}");
        }
        return text.ToString();
    }

    private void LoadHistory()
    {
        History.Clear();
        foreach (var record in _deliveryLog.GetRecent(_account.Id, 50))
            History.Add(new DeliveryRecordViewModel(record));
        OnPropertyChanged(nameof(HasHistory));
    }

    private List<string> ParseSelectors() =>
        DkimSelectors.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
