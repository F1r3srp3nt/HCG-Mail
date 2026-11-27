using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.Mail.App.Services;
using HCG.Mail.Core.Delivery;
using HCG.Mail.Core.Security;
using HCG.Mail.Core.Services;
using HCG.Mail.Infrastructure;
using HCG.Mail.Infrastructure.Logging;

namespace HCG.Mail.App.ViewModels;

/// <summary>One line of the Connection page: what was checked, the result, and when it was established.</summary>
public sealed record HealthRow(string Title, HealthResult Result, string Detail)
{
    public string ResultLabel => Result switch
    {
        HealthResult.Ok => "OK",
        HealthResult.Problem => "Problem",
        HealthResult.Warning => "Attention",
        _ => "Not verified",
    };

    public bool IsOk => Result == HealthResult.Ok;
    public bool IsProblem => Result == HealthResult.Problem;
    public bool IsWarning => Result == HealthResult.Warning;
    public bool IsUnknown => Result == HealthResult.NotVerified;
}

public enum HealthResult { NotVerified, Ok, Warning, Problem }

/// <summary>
/// Mail Health › Connection for one account. Every row is something HCG Mail observed or measured: the live session,
/// an explicit connection check (the same six-step test as account setup, using the stored password), the local send
/// history and the files HCG Mail keeps. Nothing is shown as healthy because it is configured; what has not been
/// verified says so.
/// </summary>
public sealed partial class ConnectionHealthViewModel : ObservableObject
{
    private readonly AccountViewModel _account;
    private readonly IConnectionTester _tester;
    private readonly ICredentialVault _vault;
    private readonly IDeliveryLog _deliveryLog;
    private readonly AppPaths _paths;
    private ConnectionTestReport? _report;
    private DateTimeOffset? _reportAt;
    private CancellationTokenSource? _cts;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckConnectionCommand))] private bool _isChecking;
    [ObservableProperty] private string _checkStatus = "The connection has not been checked in this window yet.";

    public ConnectionHealthViewModel(AccountViewModel account, IConnectionTester tester, ICredentialVault vault, IDeliveryLog deliveryLog, AppPaths paths)
    {
        _account = account;
        _tester = tester;
        _vault = vault;
        _deliveryLog = deliveryLog;
        _paths = paths;
        _account.PropertyChanged += (_, _) => UiThread.Post(Rebuild);
        Rebuild();
    }

    public ObservableCollection<HealthRow> Rows { get; } = [];

    public AccountHealth Health => _account.Health;

    public string StateTitle => Health switch
    {
        AccountHealth.Connected => "Connected",
        AccountHealth.Connecting => "Connecting…",
        AccountHealth.Degraded => "Connected, with problems",
        AccountHealth.Offline => "Offline — the mail server cannot be reached",
        AccountHealth.AuthenticationFailed => "Sign-in failed",
        AccountHealth.SecurityProblem => "Connection refused for security",
        AccountHealth.PasswordNeeded => "Password needed",
        _ => "Not connected",
    };

    public string StateExplanation => Health switch
    {
        AccountHealth.Connected => "The incoming server accepted the sign-in and the folders are available.",
        AccountHealth.Connecting => "HCG Mail is connecting to the incoming server.",
        AccountHealth.Degraded => "The account is connected, but an operation failed afterwards: " + (_account.LastErrorMessage ?? "see the last error below."),
        AccountHealth.Offline => "HCG Mail will try again on the next refresh. Check the network connection, or check the server name in Account settings.",
        AccountHealth.AuthenticationFailed => "The server refused the user name or password. Many providers require an app password for IMAP and SMTP.",
        AccountHealth.SecurityProblem => "The server's encryption or certificate did not pass validation, so HCG Mail did not send your password. Check the server name; HCG Mail never accepts an invalid certificate.",
        AccountHealth.PasswordNeeded => "No password is stored for this account on this device.",
        _ => "This account is not connected.",
    };

    public string StateBadge => IsHealthy ? "Healthy" : IsFailing ? "Problem" : "Attention";

    public bool IsHealthy => Health == AccountHealth.Connected;
    public bool IsDegraded => Health is AccountHealth.Degraded or AccountHealth.Connecting or AccountHealth.PasswordNeeded or AccountHealth.NotConnected;
    public bool IsFailing => Health is AccountHealth.Offline or AccountHealth.AuthenticationFailed or AccountHealth.SecurityProblem;

    private bool CanCheck() => !IsChecking && !_account.UsesOAuth;

    /// <summary>Runs the six-step check (DNS, TCP, TLS + certificate, IMAP sign-in, folders, SMTP sign-in). Sends no email.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckConnectionAsync()
    {
        string? password;
        try
        {
            password = await _vault.GetAsync(_account.Account.CredentialKey);
        }
        catch (CredentialVaultException ex)
        {
            CheckStatus = "The stored password could not be read: " + ex.Message;
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            CheckStatus = "No password is stored for this account. Enter it in Account settings, then check again.";
            return;
        }

        IsChecking = true;
        CheckStatus = "Checking… no email is sent.";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            _report = await _tester.RunAsync(_account.Account, password, null, _cts.Token);
            _reportAt = DateTimeOffset.Now;
            CheckStatus = _report.Succeeded
                ? $"All checks passed at {_reportAt.Value.ToLocalTime():t}."
                : $"Some checks did not pass ({_reportAt.Value.ToLocalTime():t}). See the rows marked Problem.";
        }
        catch (OperationCanceledException)
        {
            CheckStatus = "The check took too long and was stopped.";
        }
        finally
        {
            IsChecking = false;
            _cts.Dispose();
            _cts = null;
            Rebuild();
        }
    }

    [RelayCommand]
    private void CancelCheck() => _cts?.Cancel();

    private void Rebuild()
    {
        Rows.Clear();
        var a = _account;
        var at = _reportAt is { } t ? $" (checked {t.ToLocalTime():t})" : "";
        var step = (ConnectionTestStepKind kind) => _report?.Steps.FirstOrDefault(s => s.Kind == kind);
        HealthRow FromStep(string title, ConnectionTestStepKind kind, string notChecked)
        {
            if (step(kind) is not { } s || s.Status is ConnectionTestStatus.Pending or ConnectionTestStatus.Running)
                return new HealthRow(title, HealthResult.NotVerified, notChecked);
            return s.Status switch
            {
                ConnectionTestStatus.Passed => new HealthRow(title, HealthResult.Ok, s.Detail + at),
                ConnectionTestStatus.Failed => new HealthRow(title, HealthResult.Problem, s.Detail + at),
                _ => new HealthRow(title, HealthResult.NotVerified, "Not tested: " + s.Detail + at),
            };
        }

        // IMAP sign-in: a live, signed-in session proves it; otherwise only the explicit check can tell.
        Rows.Add(a.IsConnected && a.LastConnectedAt is { } connected && _report is null
            ? new HealthRow("Incoming server sign-in (IMAP)", HealthResult.Ok, $"Signed in at {connected.ToLocalTime():t} ({a.Account.Imap.Host}:{a.Account.Imap.Port}).")
            : FromStep("Incoming server sign-in (IMAP)", ConnectionTestStepKind.ImapAuthentication, "Not verified yet — use Check connection."));

        var lastSend = _deliveryLog.GetRecent(a.Account.Id, 50).FirstOrDefault(r => r.State == DeliveryState.Accepted);
        Rows.Add(_report is null && lastSend is not null
            ? new HealthRow("Outgoing server sign-in (SMTP)", HealthResult.Ok, $"The outgoing server accepted a message at {lastSend.Timestamp.ToLocalTime():g}.")
            : FromStep("Outgoing server sign-in (SMTP)", ConnectionTestStepKind.Smtp, "Not verified yet — use Check connection (it signs in without sending anything)."));

        Rows.Add(FromStep("Encryption (TLS) and server certificate", ConnectionTestStepKind.Tls,
            a.IsConnected
                ? "The current connection is encrypted and its certificate was validated when it was opened (HCG Mail never connects otherwise). Use Check connection to see the certificate details."
                : "Not verified yet — use Check connection."));

        var folderCount = a.AllFolders.Count(f => f.IsAvailable);
        Rows.Add(a.IsConnected && folderCount > 0 && _report is null
            ? new HealthRow("Folder discovery", HealthResult.Ok, $"{folderCount} folders found on the server.")
            : FromStep("Folder discovery", ConnectionTestStepKind.FolderAccess, "Not verified yet — connect the account or use Check connection."));

        Rows.Add(a.LastSyncedAt is { } synced
            ? new HealthRow("Last successful synchronization", HealthResult.Ok, synced.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : new HealthRow("Last successful synchronization", HealthResult.NotVerified, "No folder has been loaded without errors since HCG Mail started."));

        Rows.Add(lastSend is not null
            ? new HealthRow("Last successful send", HealthResult.Ok, $"{lastSend.Timestamp.ToLocalTime():g} — accepted by the outgoing server (delivery to the recipient cannot be confirmed by SMTP).")
            : new HealthRow("Last successful send", HealthResult.NotVerified, "No message has been accepted by the outgoing server from this device yet."));

        Rows.Add(a.LastErrorAt is { } errorAt
            ? new HealthRow("Last connection error", errorAt > (a.LastConnectedAt ?? DateTimeOffset.MinValue) ? HealthResult.Problem : HealthResult.Warning,
                $"{errorAt.ToLocalTime():g}: {a.LastErrorMessage}")
            : new HealthRow("Last connection error", HealthResult.Ok, "No errors since HCG Mail started."));

        Rows.Add(new HealthRow("Retry", a.Health is AccountHealth.Offline ? HealthResult.Warning : HealthResult.Ok,
            "A lost connection is reopened once automatically for the operation in progress. Otherwise HCG Mail reconnects on the next refresh (F5, or automatic checking if it is turned on in Settings). Sending is never retried automatically."));

        Rows.Add(LocalStorageRow());

        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateExplanation));
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(IsDegraded));
        OnPropertyChanged(nameof(IsFailing));
        OnPropertyChanged(nameof(StateBadge));
    }

    /// <summary>The files HCG Mail keeps: readable and valid? Messages themselves are never stored on the device.</summary>
    private HealthRow LocalStorageRow()
    {
        var problems = new List<string>();
        long size = 0;
        foreach (var (name, path) in new[] { ("settings", _paths.SettingsFile), ("accounts", _paths.AccountsFile), ("send history", _paths.DeliveryHistoryFile) })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var bytes = File.ReadAllBytes(path);
                size += bytes.Length;
                using var _ = JsonDocument.Parse(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                problems.Add($"the {name} file cannot be read ({ex.GetType().Name})");
            }
        }
        long logs = 0;
        try
        {
            if (Directory.Exists(_paths.LogDirectory))
                logs = new DirectoryInfo(_paths.LogDirectory).EnumerateFiles().Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add("the log folder cannot be read");
        }
        var summary = $"Settings, accounts and send history: {size / 1024.0:0.#} KB; logs: {logs / 1024.0:0.#} KB (kept 7 days). Messages are not stored on this device; they stay on the server.";
        return problems.Count == 0
            ? new HealthRow("Local data", HealthResult.Ok, summary)
            : new HealthRow("Local data", HealthResult.Problem, "Problem: " + string.Join("; ", problems) + ". " + summary);
    }

    /// <summary>
    /// Plain-text diagnostics for support. Never contains passwords, tokens or message content (none of them are part of
    /// the rows), and e-mail addresses are reduced to their domain by the same redaction as the log file.
    /// </summary>
    public string BuildDiagnosticsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("HCG Mail — connection diagnostics");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        sb.AppendLine($"App: {typeof(ConnectionHealthViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion} · {Environment.OSVersion}");
        sb.AppendLine($"Account: {_account.Account.EmailAddress} · {_account.ProviderLabel}");
        sb.AppendLine($"Incoming: {_account.Account.Imap} · Outgoing: {_account.Account.Smtp}");
        sb.AppendLine($"State: {StateTitle}");
        foreach (var row in Rows)
            sb.AppendLine($"- {row.Title}: {row.ResultLabel} — {row.Detail}");
        if (_report is not null)
        {
            sb.AppendLine("Connection check steps:");
            foreach (var s in _report.Steps)
                sb.AppendLine($"  {s.Title}: {s.Status} — {s.Detail}");
        }
        return LogRedactor.Redact(sb.ToString());
    }
}
