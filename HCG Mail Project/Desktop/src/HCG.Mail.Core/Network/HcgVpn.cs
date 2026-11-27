namespace HCG.Mail.Core.Network;

public enum HcgVpnState
{
    NotInstalled,
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// Optional connection point for the future HCG VPN service. HCG Mail never depends on it: without a VPN, or with any other
/// VPN, mail works exactly the same, and certificate validation is never relaxed because a VPN is on. An implementation is
/// registered by the host application only; HCG Mail never installs, starts or downloads a VPN by itself, and gives it no
/// credentials or message content. The Network Doctor shows its state and, when the network blocks mail, mentions it as one
/// option next to using another network.
/// </summary>
public interface IHcgVpn
{
    HcgVpnState State { get; }

    string DisplayName { get; }

    event EventHandler? StateChanged;
}

/// <summary>This build: HCG VPN is not available.</summary>
public sealed class NoHcgVpn : IHcgVpn
{
    public HcgVpnState State => HcgVpnState.NotInstalled;

    public string DisplayName => "HCG VPN";

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }
}
