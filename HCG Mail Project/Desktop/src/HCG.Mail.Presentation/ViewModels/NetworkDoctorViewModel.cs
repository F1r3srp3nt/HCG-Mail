using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Accounts;
using HCG.Mail.Core.Compose;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Security;
using HCG.Mail.Email.Diagnostics;
using HCG.Mail.Infrastructure.Logging;

namespace HCG.Mail.App.ViewModels;

public sealed class NetworkCheckRowViewModel(NetworkCheckRow row)
{
    public string Title => row.Title;
    public string Detail => row.Detail;
    public string ResultLabel => row.Result switch
    {
        NetworkCheckResult.Ok => "OK",
        NetworkCheckResult.Problem => "Problem",
        NetworkCheckResult.Warning => "Warning",
        NetworkCheckResult.Info => "Info",
        _ => "Not tested",
    };
    public bool IsOk => row.Result == NetworkCheckResult.Ok;
    public bool IsProblem => row.Result == NetworkCheckResult.Problem;
    public bool IsWarning => row.Result == NetworkCheckResult.Warning;
}

public sealed class NetworkEventViewModel(NetworkEvent e)
{
    public string Time => e.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string Description => e.Description;
}

public sealed class OutboxItemViewModel(OutboxItem item)
{
    public OutboxItem Item => item;
    public string Subject => item.Description;
    public string Recipients => string.Join(", ", item.Message.To.Concat(item.Message.Cc).Concat(item.Message.Bcc).Select(a => a.Address));
    public string Kind => item.Kind == OutboxKind.Send ? "Message" : "Draft";
    public string StateText => item.State switch
    {
        OutboxState.Waiting => item.Kind == OutboxKind.Send ? "Waiting to be sent" : "Waiting to be copied to Drafts",
        OutboxState.Sending => "Sending…",
        OutboxState.NeedsReview => "May already have been sent — check Sent",
        _ => "Not sent",
    };
    public string Detail => $"Queued {item.QueuedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}" + (item.LastError is { } e ? " · " + e : "");
    public bool CanSendAgain => item.State is OutboxState.NeedsReview or OutboxState.Failed;
}

/// <summary>
/// Mail Health › Network Doctor: finds out why mail does or does not work on the current network and says whether it is the
/// network, the password, the server or HCG Mail. Shows recent network transitions, VPN state, what waits in the Outbox, and
/// — when a network blocks the outgoing port — provider-documented alternatives that work here.
/// </summary>
public sealed partial class NetworkDoctorViewModel : ObservableObject
{
    private readonly AccountViewModel _account;
    private readonly NetworkDoctor _doctor;
    private readonly ICredentialVault _vault;
    private readonly INetworkMonitor _monitor;
    private readonly IHcgVpn _vpn;
    private readonly OutboxService? _outbox;
    private readonly Func<AccountViewModel, ServerEndpoint, Task>? _applySmtp;
    private readonly IUserPrompts? _prompts;
    private NetworkDoctorReport? _report;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(UseAlternativeCommand))] private bool _isRunning;
    [ObservableProperty] private string _statusText = "The Network Doctor has not run in this window yet.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDiagnosis), nameof(Headline), nameof(Explanation), nameof(CategoryLabel), nameof(IsHealthy), nameof(IsProblem), nameof(IsWarning))]
    private NetworkDiagnosis? _diagnosis;

    public NetworkDoctorViewModel(AccountViewModel account, NetworkDoctor doctor, ICredentialVault vault, INetworkMonitor monitor, IHcgVpn vpn,
        OutboxService? outbox = null, Func<AccountViewModel, ServerEndpoint, Task>? applySmtp = null, IUserPrompts? prompts = null)
    {
        _account = account;
        _doctor = doctor;
        _vault = vault;
        _monitor = monitor;
        _vpn = vpn;
        _outbox = outbox;
        _applySmtp = applySmtp;
        _prompts = prompts;
        _monitor.Changed += (_, _) => UiThread.Post(RefreshEvents);
        if (_outbox is not null)
            _outbox.Changed += (_, _) => UiThread.Post(RefreshOutbox);
        RefreshEvents();
        RefreshOutbox();
    }

    public ObservableCollection<NetworkCheckRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Actions { get; } = [];
    public ObservableCollection<AlternativeEndpoint> Alternatives { get; } = [];
    public ObservableCollection<NetworkEventViewModel> Events { get; } = [];
    public ObservableCollection<OutboxItemViewModel> Outbox { get; } = [];

    public bool HasDiagnosis => Diagnosis is not null;
    public string Headline => Diagnosis?.Headline ?? "";
    public string Explanation => Diagnosis?.Explanation ?? "";
    public string CategoryLabel => Diagnosis?.CategoryLabel ?? "";
    public bool IsHealthy => Diagnosis?.Category == VerdictCategory.Healthy;
    public bool IsProblem => Diagnosis?.Category is VerdictCategory.NoConnection or VerdictCategory.NetworkRestriction or VerdictCategory.Credentials or VerdictCategory.ServerProblem or VerdictCategory.ApplicationProblem;
    public bool IsWarning => Diagnosis?.Category == VerdictCategory.Unknown;
    public bool HasAlternatives => Alternatives.Count > 0;
    public bool HasOutbox => Outbox.Count > 0;
    public bool IsAvailable => !_account.UsesOAuth;
    public string PrivacyText => _doctor.Options.ContactedHostsText;

    public string VpnText => _vpn.State switch
    {
        HcgVpnState.Connected => $"{_vpn.DisplayName} is connected.",
        HcgVpnState.Connecting => $"{_vpn.DisplayName} is connecting.",
        HcgVpnState.Disconnected => $"{_vpn.DisplayName} is installed but not connected.",
        _ => $"{_vpn.DisplayName} is not installed. HCG Mail works without it; it is an optional way to reach mail from networks that block it.",
    };

    public string RecoveryText => _account.RecoveryText ?? (_account.IsConnected ? "Connected." : _account.StatusText);

    private bool CanRun() => !IsRunning && IsAvailable;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        string? password = null;
        try
        {
            password = await _vault.GetAsync(_account.Account.CredentialKey);
        }
        catch (CredentialVaultException)
        {
            // Network checks still run; sign-in is reported as not tested.
        }
        IsRunning = true;
        StatusText = "Checking… nothing is sent and no settings are changed.";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var finished = false; // progress reports arrive asynchronously and must not overwrite the final status
            var progress = new Progress<string>(text =>
            {
                if (!finished)
                    StatusText = text;
            });
            _report = await _doctor.RunAsync(_account.Account, password, _account.LastErrorKind, _account.LastConnectedAt is not null || _account.LastSyncedAt is not null, progress, cts.Token);
            finished = true;
            Diagnosis = _report.Diagnosis;
            Rows.Clear();
            foreach (var row in _report.Rows)
                Rows.Add(new NetworkCheckRowViewModel(row));
            Actions.Clear();
            foreach (var action in _report.Diagnosis.Actions)
                Actions.Add(action);
            Alternatives.Clear();
            if (_applySmtp is not null)
                foreach (var alt in _report.Diagnosis.WorkingAlternatives)
                    Alternatives.Add(alt);
            OnPropertyChanged(nameof(HasAlternatives));
            StatusText = $"Checked at {_report.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)} in {_report.Duration.TotalSeconds:0.0} s.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "The check took too long and was stopped.";
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(RecoveryText));
            RefreshEvents();
        }
    }

    private bool CanUseAlternative(AlternativeEndpoint? alt) => !IsRunning && alt is not null && _applySmtp is not null;

    /// <summary>Switches the outgoing server to a documented alternative that works on this network (after confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanUseAlternative))]
    private async Task UseAlternativeAsync(AlternativeEndpoint? alt)
    {
        if (alt is null || _applySmtp is null)
            return;
        if (_prompts is not null && !await _prompts.ConfirmAsync("Change the outgoing server?",
                $"Send through {alt.Label} instead of {_account.Account.Smtp.Host} port {_account.Account.Smtp.Port}? ({alt.Source}.) " +
                "The connection stays fully encrypted and the certificate is still verified. You can change it back in the account settings.",
                "Use this"))
            return;
        await _applySmtp(_account, alt.Endpoint);
        Alternatives.Remove(alt);
        OnPropertyChanged(nameof(HasAlternatives));
        StatusText = $"Outgoing server changed to {alt.Label}.";
    }

    [RelayCommand]
    private async Task SendAgainAsync(OutboxItemViewModel? item)
    {
        if (item is null || _outbox is null)
            return;
        if (item.Item.State == OutboxState.NeedsReview && _prompts is not null &&
            !await _prompts.ConfirmAsync("Send this message again?",
                "It may already have been sent. Check your Sent folder (or ask a recipient) first: sending again may deliver it twice.",
                "Send again", destructive: true))
            return;
        _outbox.Requeue(item.Item);
        if (_account.IsConnected)
            await _outbox.FlushAsync(_account);
    }

    [RelayCommand]
    private async Task DiscardAsync(OutboxItemViewModel? item)
    {
        if (item is null || _outbox is null)
            return;
        if (_prompts is not null && !await _prompts.ConfirmAsync("Discard this message?", $"“{item.Subject}” will be deleted from the Outbox without being sent.", "Discard", destructive: true))
            return;
        _outbox.Discard(item.Item);
    }

    private void RefreshEvents()
    {
        Events.Clear();
        foreach (var e in _monitor.RecentEvents.Reverse().Take(15))
            Events.Add(new NetworkEventViewModel(e));
    }

    private void RefreshOutbox()
    {
        Outbox.Clear();
        if (_outbox is not null)
            foreach (var item in _outbox.ItemsFor(_account.Account.Id))
                Outbox.Add(new OutboxItemViewModel(item));
        OnPropertyChanged(nameof(HasOutbox));
    }

    /// <summary>Plain-text report for support: no passwords, tokens or message content; addresses are reduced to their domain.</summary>
    public string BuildReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("HCG Mail — Network Doctor");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        sb.AppendLine($"App: {typeof(NetworkDoctorViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion} · {Environment.OSVersion}");
        sb.AppendLine($"Account: {_account.Account.EmailAddress} · incoming {_account.Account.Imap} · outgoing {_account.Account.Smtp}");
        sb.AppendLine($"Account state: {_account.StatusText} {_account.RecoveryText}".TrimEnd());
        if (Diagnosis is { } d)
        {
            sb.AppendLine($"Verdict: {d.CategoryLabel} — {d.Headline} ({d.Verdict})");
            sb.AppendLine(d.Explanation);
            foreach (var a in d.Actions)
                sb.AppendLine("  • " + a);
        }
        foreach (var row in Rows)
            sb.AppendLine($"- {row.Title}: {row.ResultLabel} — {row.Detail}");
        sb.AppendLine("Recent network events:");
        foreach (var e in Events)
            sb.AppendLine($"  {e.Time} {e.Description}");
        sb.AppendLine($"VPN: {VpnText}");
        sb.AppendLine($"Outbox: {Outbox.Count} item(s)" + (Outbox.Count > 0 ? " — " + string.Join("; ", Outbox.Select(o => $"{o.Kind}: {o.StateText}")) : ""));
        return LogRedactor.Redact(sb.ToString());
    }
}
