using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PrinterAgent.Infrastructure.Networking;

internal static class LocalSubnetEnumerator
{
    /// <summary>IPv4 host addresses (1–254) for each local /24 unicast (v1: /24 only).</summary>
    internal static IReadOnlyList<string> EnumerateLikelyHostAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var ip = ua.Address;
                if (IPAddress.IsLoopback(ip))
                    continue;

                // Do not require PrefixLength==24. Wi-Fi /28, /25, etc. still use the same "class C" of the host
                // for on-link recovery; strict /24 excluded many hotspot/LAN interfaces.
                if (ua.PrefixLength is < 8 or > 30)
                    continue;

                var b = ip.GetAddressBytes();
                if (b.Length != 4)
                    continue;

                for (var last = 1; last <= 254; last++)
                {
                    if (last == b[3])
                        continue; // optional: skip self
                    set.Add($"{b[0]}.{b[1]}.{b[2]}.{last}");
                }
            }
        }

        return set.ToList();
    }
}
