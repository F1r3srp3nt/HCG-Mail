using System.Net.NetworkInformation;
using System.Net.Sockets;
using HCG.Mail.Core.Network;

namespace HCG.Mail.Infrastructure.Network;

/// <summary>The computer's active network interfaces, and which of them look like a VPN. Reads local state only.</summary>
public sealed class SystemNetworkInterfaces : INetworkInterfaces
{
    private static readonly string[] VpnWords =
        ["vpn", "wireguard", "openvpn", "tailscale", "zerotier", "anyconnect", "globalprotect", "fortinet", "forticlient", "nordlynx", "ipsec", "pulse secure", "wintun", "tap-windows"];

    private static readonly string[] TunnelPrefixes = ["utun", "tun", "tap", "wg", "ppp", "ipsec", "gpd"];

    public IReadOnlyList<NetworkInterfaceInfo> GetActive()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }

        var result = new List<NetworkInterfaceInfo>();
        foreach (var nic in all)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToList();
                var routable = addresses.Any(a =>
                    (a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)) ||
                    (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal));
                if (!routable)
                    continue; // e.g. macOS system utun interfaces with only link-local addresses
                result.Add(new NetworkInterfaceInfo(nic.Name, nic.Description, Describe(nic.NetworkInterfaceType), true, LooksLikeVpn(nic.Name, nic.Description, nic.NetworkInterfaceType)));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // An interface that disappears while being read is simply skipped.
            }
        }
        return result;
    }

    public static bool LooksLikeVpn(string name, string description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel)
            return true;
        var text = (name + " " + description).ToLowerInvariant();
        return VpnWords.Any(text.Contains) || TunnelPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
        _ => type.ToString(),
    };
}
