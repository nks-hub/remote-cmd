using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>An address of this machine that other machines might reach the relay at.</summary>
public sealed record RelayAddress(string Address, string Interface, string Kind);

/// <summary>
/// This machine's IPv4 addresses, LAN first, then public, VPN and virtual adapters. Enumeration order
/// alone is not enough: on a machine with WireGuard or Hyper-V the first private address is often a
/// tunnel or a virtual switch, which machines on the office network cannot reach.
/// </summary>
public static class RelayAddresses
{
    public const string Lan = "lan";
    public const string Public = "public";
    public const string Vpn = "vpn";
    public const string Virtual = "virtual";

    private static readonly string[] Order = [Lan, Public, Vpn, Virtual];

    public static IReadOnlyList<RelayAddress> Candidates()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n =>
            {
                var ip = n.GetIPProperties();
                var hasGateway = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                return ip.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(a.Address))
                    .Select(a => new RelayAddress(a.Address.ToString(), n.Name,
                        Classify(n.NetworkInterfaceType, n.Name, n.Description, a.Address, hasGateway)));
            })
            .OrderBy(a => Array.IndexOf(Order, a.Kind))
            .ToList();

    /// <summary>
    /// What kind of network an interface address belongs to, from what the OS says about it. A
    /// virtual adapter with a default gateway is the way out to a real network — a Hyper-V external
    /// switch carries the machine's LAN address — so only gateway-less ones count as virtual.
    /// </summary>
    public static string Classify(NetworkInterfaceType type, string name, string description, IPAddress address, bool hasGateway)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            || ContainsAny(text, "wireguard", "wintun", "vpn", "l2tp", "openvpn", "tap-windows", "tailscale", "zerotier")
            || name.Split('_', '-', ' ').Any(part => part.Equals("wg", StringComparison.OrdinalIgnoreCase))
            || (name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) && name.Length <= 4)
            || name.StartsWith("tun", StringComparison.OrdinalIgnoreCase))
            return Vpn;
        if (!hasGateway
            && (ContainsAny(text, "vethernet", "hyper-v", "virtual", "vmware", "virtualbox", "vbox", "docker", "wsl", "default switch")
                || name.StartsWith("br-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("veth", StringComparison.OrdinalIgnoreCase)))
            return Virtual;
        return IsPrivate(address) ? Lan : Public;
    }

    private static bool ContainsAny(string text, params string[] words) => words.Any(text.Contains);

    private static bool IsPrivate(IPAddress a)
        => a.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..] or [100, >= 64 and <= 127, ..];

    private static bool IsLinkLocal(IPAddress a) => a.GetAddressBytes() is [169, 254, ..];
}
