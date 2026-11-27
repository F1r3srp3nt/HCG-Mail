using System.Net.NetworkInformation;
using HCG.Mail.Core.Network;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Network;

/// <summary>
/// Watches the operating system's network state: connections appearing and disappearing, address changes (another Wi-Fi, a
/// VPN) and sleep/wake. Wake-up is detected from the clock: a check that should run every 15 seconds and finds that minutes
/// have passed means the computer was asleep (or the app was suspended), so open mail connections are stale. No server is
/// contacted. Bursts of address changes during a Wi-Fi switch are combined into one event.
/// </summary>
public sealed class SystemNetworkMonitor : INetworkMonitor, IDisposable
{
    public static readonly TimeSpan WakeCheckInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SleepThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private const int MaxEvents = 40;

    private readonly TimeProvider _time;
    private readonly INetworkInterfaces _interfaces;
    private readonly ILogger _logger;
    private readonly List<NetworkEvent> _events = [];
    private readonly ITimer _wakeTimer;
    private readonly ITimer _settleTimer;
    private readonly bool _subscribed;
    private DateTimeOffset _lastTick;
    private string _lastSignature;
    private bool _available;

    public SystemNetworkMonitor(TimeProvider time, INetworkInterfaces interfaces, ILogger<SystemNetworkMonitor> logger)
    {
        _time = time;
        _interfaces = interfaces;
        _logger = logger;
        _available = ReadAvailability();
        _lastSignature = Signature();
        _lastTick = time.GetUtcNow();
        Record(NetworkEventKind.Started, _available ? "Network available: " + DescribeInterfaces() : "No network connection");

        _settleTimer = time.CreateTimer(_ => OnSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _wakeTimer = time.CreateTimer(_ => OnWakeCheck(), null, WakeCheckInterval, WakeCheckInterval);
        try
        {
            NetworkChange.NetworkAddressChanged += OnSystemChange;
            NetworkChange.NetworkAvailabilityChanged += OnSystemAvailability;
            _subscribed = true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            _logger.LogInformation("Network change notifications are not available on this platform ({Error})", ex.GetType().Name);
        }
    }

    public bool IsNetworkAvailable => _available;

    public IReadOnlyList<NetworkEvent> RecentEvents
    {
        get
        {
            lock (_events)
                return _events.ToList();
        }
    }

    public event EventHandler<NetworkEvent>? Changed;

    private void OnSystemChange(object? sender, EventArgs e) => _settleTimer.Change(Settle, Timeout.InfiniteTimeSpan);

    private void OnSystemAvailability(object? sender, NetworkAvailabilityEventArgs e) => _settleTimer.Change(Settle, Timeout.InfiniteTimeSpan);

    /// <summary>Re-reads the network state after a burst of system notifications has settled.</summary>
    internal void OnSettled()
    {
        var available = ReadAvailability();
        var signature = Signature();
        if (available == _available && signature == _lastSignature)
            return;
        var wasAvailable = _available;
        _available = available;
        _lastSignature = signature;
        if (!available)
            Raise(NetworkEventKind.Unavailable, "Network connection lost");
        else if (!wasAvailable)
            Raise(NetworkEventKind.Available, "Network connection available: " + DescribeInterfaces());
        else
            Raise(NetworkEventKind.Changed, "Network changed: " + DescribeInterfaces());
    }

    internal void OnWakeCheck()
    {
        var now = _time.GetUtcNow();
        var gap = now - _lastTick;
        _lastTick = now;
        if (gap > WakeCheckInterval + SleepThreshold)
        {
            _available = ReadAvailability();
            _lastSignature = Signature();
            Raise(NetworkEventKind.Resumed, $"Woke up after about {Describe(gap)}; reconnecting");
        }
    }

    private void Raise(NetworkEventKind kind, string description)
    {
        var e = Record(kind, description);
        _logger.LogInformation("Network event {Kind}: {Description}", kind, description);
        try
        {
            Changed?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A network event handler failed");
        }
    }

    private NetworkEvent Record(NetworkEventKind kind, string description)
    {
        var e = new NetworkEvent(_time.GetLocalNow(), kind, description);
        lock (_events)
        {
            _events.Add(e);
            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }
        return e;
    }

    private static bool ReadAvailability()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            return true; // unknown: assume available and let the connection attempt decide
        }
    }

    private string Signature() => string.Join("|", _interfaces.GetActive().Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));

    private string DescribeInterfaces()
    {
        var active = _interfaces.GetActive();
        if (active.Count == 0)
            return "no active interface";
        return string.Join(", ", active.Select(i => i.LooksLikeVpn ? $"VPN ({i.Name})" : $"{i.Type} ({i.Name})"));
    }

    private static string Describe(TimeSpan gap) =>
        gap.TotalHours >= 1 ? $"{gap.TotalHours:0.#} hours" : $"{Math.Max(1, (int)Math.Round(gap.TotalMinutes))} minutes";

    public void Dispose()
    {
        if (_subscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnSystemChange;
            NetworkChange.NetworkAvailabilityChanged -= OnSystemAvailability;
        }
        _wakeTimer.Dispose();
        _settleTimer.Dispose();
    }
}
