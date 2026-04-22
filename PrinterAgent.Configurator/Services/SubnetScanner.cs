using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PrinterAgent.Configurator.Services;

public sealed record NicSubnetOption(
    string Description,
    string InterfaceId,
    IPAddress IPv4,
    int PrefixLength,
    bool HasDefaultGateway)
{
    public string CidrDisplay
    {
        get
        {
            var network = IpMath.GetNetworkAddress(IPv4, PrefixLength);
            return $"{IPv4}/{PrefixLength} (rețea {network}/{PrefixLength})";
        }
    }
}

public static class IpMath
{
    public static IPAddress GetNetworkAddress(IPAddress ip, int prefixLength)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
            throw new ArgumentException("Only IPv4 și prefix 0–32 sunt suportate.");

        Span<byte> ipBytes = stackalloc byte[4];
        ip.GetAddressBytes().CopyTo(ipBytes);
        var ipNum = BinaryPrimitives.ReadUInt32BigEndian(ipBytes);
        var mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
        var network = ipNum & mask;
        BinaryPrimitives.WriteUInt32BigEndian(ipBytes, network);
        return new IPAddress(ipBytes);
    }

    public static IEnumerable<IPAddress> EnumerateHostAddresses(IPAddress ipv4, int prefixLength)
    {
        if (ipv4.AddressFamily != AddressFamily.InterNetwork)
            yield break;

        var ipBytes = ipv4.GetAddressBytes();
        var ipNum = BinaryPrimitives.ReadUInt32BigEndian(ipBytes);
        var mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
        var network = ipNum & mask;
        var broadcast = network | (~mask & 0xFFFFFFFFu);

        // /31, /32: fără model broadcast clasic — probăm doar adresa locală
        if (broadcast <= network + 1)
        {
            yield return ipv4;
            yield break;
        }

        for (var host = network + 1; host < broadcast; host++)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, host);
            yield return new IPAddress(bytes);
        }
    }
}

public static class LocalSubnetService
{
    public static IReadOnlyList<NicSubnetOption> GetIpv4SubnetOptions()
    {
        var list = new List<NicSubnetOption>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                          && !g.Address.Equals(IPAddress.Any));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(ua.Address))
                    continue;

                var prefix = ua.PrefixLength;
                if (prefix is <= 0 or > 32)
                    continue;

                list.Add(new NicSubnetOption(
                    ni.Name + " — " + ni.Description,
                    ni.Id,
                    ua.Address,
                    prefix,
                    hasGateway));
            }
        }

        return list
            .OrderByDescending(o => o.HasDefaultGateway)
            .ThenBy(o => o.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static NicSubnetOption? GetPreferredDefault(IReadOnlyList<NicSubnetOption> options) =>
        options.FirstOrDefault(o => o.HasDefaultGateway) ?? options.FirstOrDefault();
}

public sealed class Port9100Scanner
{
    private const int Port = 9100;
    private const int ConnectTimeoutMs = 400;
    private const int MaxParallel = 48;

    public async Task<IReadOnlyList<IPAddress>> ScanAsync(
        IPAddress localIpv4,
        int prefixLength,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var targets = IpMath.EnumerateHostAddresses(localIpv4, prefixLength).ToList();
        progress?.Report($"Se testează {targets.Count} adrese pe portul {Port}…");

        var found = new ConcurrentBag<IPAddress>();
        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await TryConnectAsync(ip, cancellationToken).ConfigureAwait(false))
                    found.Add(ip);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found
            .GroupBy(a => a.ToString())
            .Select(g => g.First())
            .OrderBy(a => BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes()), Comparer<uint>.Create((x, y) => x.CompareTo(y)))
            .ToList();
    }

    private static async Task<bool> TryConnectAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ConnectTimeoutMs);
            await client.ConnectAsync(ip, Port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
