namespace HCG.Mail.Core.Network;

public enum NetworkEventKind
{
    Started,

    /// <summary>A network connection became available.</summary>
    Available,

    /// <summary>No network connection is available any more.</summary>
    Unavailable,

    /// <summary>The computer's network addresses changed: another Wi-Fi, a cable, a VPN started or stopped.</summary>
    Changed,

    /// <summary>The computer woke up from sleep (or the app was suspended); open connections are probably dead.</summary>
    Resumed,
}

public sealed record NetworkEvent(DateTimeOffset At, NetworkEventKind Kind, string Description);

/// <summary>
/// Network transitions and sleep/wake as the operating system reports them. Reading it contacts no server: it uses only the
/// computer's own network state, so it can run all the time without any background traffic.
/// </summary>
public interface INetworkMonitor
{
    bool IsNetworkAvailable { get; }

    /// <summary>The most recent transitions, oldest first (at most a few dozen).</summary>
    IReadOnlyList<NetworkEvent> RecentEvents { get; }

    event EventHandler<NetworkEvent>? Changed;
}

/// <summary>For tests and preview mode: always available, raises events only when told to.</summary>
public sealed class ManualNetworkMonitor : INetworkMonitor
{
    private readonly List<NetworkEvent> _events = [];

    public bool IsNetworkAvailable { get; private set; } = true;

    public IReadOnlyList<NetworkEvent> RecentEvents
    {
        get
        {
            lock (_events)
                return _events.ToList();
        }
    }

    public event EventHandler<NetworkEvent>? Changed;

    public void Raise(NetworkEventKind kind, string description = "")
    {
        if (kind == NetworkEventKind.Unavailable)
            IsNetworkAvailable = false;
        else if (kind is NetworkEventKind.Available or NetworkEventKind.Changed)
            IsNetworkAvailable = true;
        var e = new NetworkEvent(DateTimeOffset.Now, kind, description);
        lock (_events)
            _events.Add(e);
        Changed?.Invoke(this, e);
    }
}

public sealed record NetworkInterfaceInfo(string Name, string Description, string Type, bool IsUp, bool LooksLikeVpn);

public interface INetworkInterfaces
{
    IReadOnlyList<NetworkInterfaceInfo> GetActive();
}
