using HCG.Mail.Core.Network;
using HCG.Mail.Infrastructure.Network;
using HCG.Mail.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.Mail.Tests.Network;

public sealed class NetworkMonitorTests
{
    private sealed class FakeInterfaces : INetworkInterfaces
    {
        public List<NetworkInterfaceInfo> Active { get; } = [new("wlan0", "Wi-Fi", "Wi-Fi", true, false)];
        public IReadOnlyList<NetworkInterfaceInfo> GetActive() => Active.ToList();
    }

    [Fact]
    public void Waking_from_sleep_is_detected_from_the_clock_and_reported_once()
    {
        var time = new ManualTimeProvider();
        using var monitor = new SystemNetworkMonitor(time, new FakeInterfaces(), NullLogger<SystemNetworkMonitor>.Instance);
        var events = new List<NetworkEvent>();
        monitor.Changed += (_, e) => events.Add(e);

        time.Advance(TimeSpan.FromMinutes(2)); // normal ticks every 15 s: no sleep
        Assert.Empty(events);

        time.Jump(TimeSpan.FromMinutes(42)); // the lid was closed
        time.Advance(SystemNetworkMonitor.WakeCheckInterval);
        var resumed = Assert.Single(events);
        Assert.Equal(NetworkEventKind.Resumed, resumed.Kind);
        Assert.Contains("42 minutes", resumed.Description);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(events);
        Assert.Contains(monitor.RecentEvents, e => e.Kind == NetworkEventKind.Started);
    }

    [Fact]
    public void A_wifi_switch_is_reported_as_a_network_change()
    {
        var time = new ManualTimeProvider();
        var nics = new FakeInterfaces();
        using var monitor = new SystemNetworkMonitor(time, nics, NullLogger<SystemNetworkMonitor>.Instance);
        var events = new List<NetworkEvent>();
        monitor.Changed += (_, e) => events.Add(e);

        nics.Active.Clear();
        nics.Active.Add(new("wlan1", "Wi-Fi", "Wi-Fi", true, false));
        nics.Active.Add(new("wg0", "WireGuard", "Tunnel", true, true));
        monitor.OnSettled();
        monitor.OnSettled(); // repeated system notifications without a real change are ignored

        var change = Assert.Single(events);
        Assert.Contains(change.Kind, new[] { NetworkEventKind.Changed, NetworkEventKind.Available, NetworkEventKind.Unavailable });
        if (change.Kind == NetworkEventKind.Changed)
            Assert.Contains("VPN (wg0)", change.Description);
    }

    [Theory]
    [InlineData("utun4", "", System.Net.NetworkInformation.NetworkInterfaceType.Unknown, true)]
    [InlineData("Ethernet 3", "WireGuard Tunnel", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, true)]
    [InlineData("Local Area Connection", "Cisco AnyConnect Secure Mobility Client", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, true)]
    [InlineData("ppp0", "", System.Net.NetworkInformation.NetworkInterfaceType.Ppp, true)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, false)]
    [InlineData("en0", "", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, false)]
    public void Vpn_interfaces_are_recognised(string name, string description, System.Net.NetworkInformation.NetworkInterfaceType type, bool vpn) =>
        Assert.Equal(vpn, SystemNetworkInterfaces.LooksLikeVpn(name, description, type));

    [Fact]
    public void Hcg_vpn_is_optional_and_absent_by_default()
    {
        IHcgVpn vpn = new NoHcgVpn();
        Assert.Equal(HcgVpnState.NotInstalled, vpn.State);
    }
}
