using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PrinterAgent.Application.Networking;

public static class LocalLanAddressResolver
{
    public static string? TryResolvePrimaryLanIPv4()
    {
        var candidates = new List<(NetworkInterface Ni, UnicastIPAddressInformation Ua)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var name = ni.Name ?? string.Empty;
            var desc = ni.Description ?? string.Empty;
            if (IsVirtualOrVpn(name, desc))
                continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(ua.Address))
                    continue;
                if (ua.PrefixLength is < 8 or > 30)
                    continue;

                candidates.Add((ni, ua));
            }
        }

        if (candidates.Count == 0)
            return null;

        candidates.Sort((a, b) => Score(b.Ni).CompareTo(Score(a.Ni)));
        return candidates[0].Ua.Address.ToString();
    }

    private static int Score(NetworkInterface ni) =>
        ni.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet => 100,
            NetworkInterfaceType.GigabitEthernet => 100,
            NetworkInterfaceType.Wireless80211 => 50,
            _ => 10,
        };

    private static bool IsVirtualOrVpn(string name, string description)
    {
        var combined = $"{name} {description}".ToLowerInvariant();
        return combined.Contains("wireguard", StringComparison.Ordinal)
               || combined.Contains("tap", StringComparison.Ordinal)
               || combined.Contains("tun", StringComparison.Ordinal)
               || combined.Contains("virtual", StringComparison.Ordinal)
               || combined.Contains("hyper-v", StringComparison.Ordinal)
               || combined.Contains("vethernet", StringComparison.Ordinal)
               || combined.Contains("wsl", StringComparison.Ordinal);
    }
}

public static class LocalPrintEndpointBuilder
{
    public static string? TryBuildBaseUrl(int port)
    {
        var ip = LocalLanAddressResolver.TryResolvePrimaryLanIPv4();
        if (string.IsNullOrWhiteSpace(ip))
            return null;

        var safePort = port is > 0 and <= 65535 ? port : 9247;
        return $"http://{ip}:{safePort}";
    }
}
