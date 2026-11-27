using System.ComponentModel;
using System.Globalization;
using HCG.Mail.App.ViewModels;
using HCG.Mail.Core.Errors;
using HCG.Mail.Core.Network;
using HCG.Mail.Core.Services;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.App.Services;

/// <summary>
/// Reconnects accounts after a connection problem instead of letting synchronization stop silently. Per account:
/// <list type="bullet">
/// <item>network, DNS, timeout, throttling and revocation-service failures are retried with bounded exponential backoff
/// (<see cref="ReconnectPolicy"/>), and at once when the network comes back, changes (another Wi-Fi, a VPN) or the computer
/// wakes up;</item>
/// <item>certificate and TLS failures (often a Wi-Fi sign-in page or an inspecting proxy) are retried only when the network
/// changes;</item>
/// <item>a refused password and unexpected errors are never retried automatically (repeated sign-ins can lock an account).</item>
/// </list>
/// While there is no network at all nothing is attempted; the next "network available" event starts the attempts. After a
/// network change or wake-up, connections of accounts that still look connected are marked stale so the next action
/// reconnects at once. The account always shows what happens next (<see cref="AccountViewModel.RecoveryText"/>).
/// </summary>
public sealed class ConnectionRecoveryService : IDisposable
{
    public static TimeSpan NetworkSettleDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly INetworkMonitor _network;
    private readonly TimeProvider _time;
    private readonly AppSettingsService _settings;
    private readonly ILogger _logger;
    private readonly ReconnectPolicy _policy;
    private readonly Func<double> _random;
    private readonly Action<Action> _post;
    private readonly Dictionary<AccountViewModel, State> _states = [];
    private Func<AccountViewModel, Task>? _reconnect;

    public ConnectionRecoveryService(INetworkMonitor network, TimeProvider time, AppSettingsService settings, ILogger<ConnectionRecoveryService> logger,
        ReconnectPolicy? policy = null, Func<double>? random = null, Action<Action>? post = null)
    {
        _network = network;
        _time = time;
        _settings = settings;
        _logger = logger;
        _policy = policy ?? ReconnectPolicy.Default;
        _random = random ?? Random.Shared.NextDouble;
        _post = post ?? UiThread.Post;
        _network.Changed += OnNetworkChanged;
    }

    private sealed class State
    {
        public int Attempts;
        public ITimer? Timer;
    }

    /// <summary>The action that reconnects an account (the same as the user's Reconnect).</summary>
    public void Start(Func<AccountViewModel, Task> reconnect) => _reconnect = reconnect;

    public void Track(AccountViewModel account)
    {
        if (_states.ContainsKey(account))
            return;
        _states[account] = new State();
        account.PropertyChanged += OnAccountChanged;
        Evaluate(account);
    }

    public void Untrack(AccountViewModel account)
    {
        if (!_states.Remove(account, out var state))
            return;
        state.Timer?.Dispose();
        account.PropertyChanged -= OnAccountChanged;
    }

    public int AttemptsFor(AccountViewModel account) => _states.TryGetValue(account, out var s) ? s.Attempts : 0;

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.Status) && sender is AccountViewModel account)
            Evaluate(account);
    }

    private void Evaluate(AccountViewModel account)
    {
        if (!_states.TryGetValue(account, out var state))
            return;
        switch (account.Status)
        {
            case AccountStatus.Connected:
                Cancel(state);
                state.Attempts = 0;
                account.SetRecovery(null, null);
                break;
            case AccountStatus.Connecting:
                Cancel(state);
                account.SetRecovery(null, null);
                break;
            case AccountStatus.NeedsPassword:
                Cancel(state);
                account.SetRecovery("Waiting for you to enter the password.", null);
                break;
            case AccountStatus.NeedsSignIn:
                Cancel(state);
                account.SetRecovery("Waiting for you to sign in again.", null);
                break;
            case AccountStatus.Error:
                Schedule(account, state, afterNetworkEvent: false);
                break;
        }
    }

    private void Schedule(AccountViewModel account, State state, bool afterNetworkEvent)
    {
        Cancel(state);
        var mode = ReconnectPolicy.ModeFor(account.LastErrorKind);
        if (!_settings.Current.ReconnectAutomatically)
        {
            account.SetRecovery("Automatic reconnection is turned off. Click Reconnect to try again.", null);
            return;
        }
        if (mode == RecoveryMode.Never)
        {
            account.SetRecovery(account.LastErrorKind is MailErrorKind.Authentication
                ? "HCG Mail does not retry a refused password by itself. Update the password, then reconnect."
                : "Click Reconnect to try again. If it keeps failing, run the Network Doctor in Mail Health.", null);
            return;
        }
        if (!_network.IsNetworkAvailable)
        {
            account.SetRecovery("Waiting for a network connection. HCG Mail reconnects as soon as one is available.", null);
            return;
        }
        if (mode == RecoveryMode.OnNetworkChange && !afterNetworkEvent)
        {
            account.SetRecovery("HCG Mail tries again when the network changes (for example after you sign in to the Wi-Fi). Click Reconnect to try now.", null);
            return;
        }

        var delay = afterNetworkEvent ? NetworkSettleDelay : _policy.DelayFor(state.Attempts, _random());
        if (!afterNetworkEvent)
            state.Attempts++;
        var at = _time.GetLocalNow() + delay;
        state.Timer = _time.CreateTimer(_ => _post(() => _ = FireAsync(account)), null, delay, Timeout.InfiniteTimeSpan);
        account.SetRecovery(afterNetworkEvent
            ? "The network changed: reconnecting now…"
            : $"Reconnecting automatically at {at.ToString("HH:mm:ss", CultureInfo.CurrentCulture)} (attempt {state.Attempts}).", at);
        _logger.LogInformation("Account {AccountId}: reconnect in {Delay} s ({Mode}, attempt {Attempt})", account.Account.Id, (int)delay.TotalSeconds, mode, state.Attempts);
    }

    private async Task FireAsync(AccountViewModel account)
    {
        if (!_states.ContainsKey(account) || account.Status != AccountStatus.Error || _reconnect is null)
            return;
        try
        {
            await _reconnect(account);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic reconnect of account {AccountId} failed unexpectedly", account.Account.Id);
        }
    }

    private void OnNetworkChanged(object? sender, NetworkEvent e) => _post(() =>
    {
        foreach (var (account, state) in _states.ToList())
        {
            if (e.Kind == NetworkEventKind.Unavailable)
            {
                if (account.Status == AccountStatus.Error)
                {
                    Cancel(state);
                    account.SetRecovery("Waiting for a network connection. HCG Mail reconnects as soon as one is available.", null);
                }
                continue;
            }
            if (e.Kind is NetworkEventKind.Changed or NetworkEventKind.Resumed)
            {
                if (account.Session is IOfflineAwareSession offlineAware)
                    offlineAware.MarkConnectionStale();
                else if (account.Session is IStaleConnectionAware stale)
                    stale.MarkConnectionStale();
            }
            if (account.Status == AccountStatus.Error && e.Kind is NetworkEventKind.Available or NetworkEventKind.Changed or NetworkEventKind.Resumed)
            {
                // A new network is a fresh start: the backoff begins again from the shortest pause.
                state.Attempts = 0;
                if (_settings.Current.ReconnectAutomatically && ReconnectPolicy.ModeFor(account.LastErrorKind) != RecoveryMode.Never)
                    Schedule(account, state, afterNetworkEvent: true);
            }
        }
    });

    private static void Cancel(State state)
    {
        state.Timer?.Dispose();
        state.Timer = null;
    }

    public void Dispose()
    {
        _network.Changed -= OnNetworkChanged;
        foreach (var account in _states.Keys.ToList())
            Untrack(account);
    }
}
