using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace PrinterAgent.Infrastructure.Networking;

internal static class IpHlpNative
{
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetIpNetTable(IntPtr pIpNetTable, ref int dwSize, bool bOrder);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref int phyAddrLen);

    /// <summary>SendARP first (cheap); full ARP table walk if needed (e.g. SendARP error on some NIC drivers).</summary>
    internal static bool TryGetMacForIPv4(IPAddress ipv4, out string macColonHex)
    {
        if (TrySendArpForIpv4(ipv4, out macColonHex))
            return true;
        return TryGetMacFromArpTableForIpv4(ipv4, out macColonHex);
    }

    /// <summary>Looks up the IPv4 neighbor in the system ARP cache (GetIpNetTable) — best after a successful connect.</summary>
    internal static bool TryGetMacFromArpTableForIpv4(IPAddress ipv4, out string macColonHex)
    {
        macColonHex = string.Empty;
        var want = ipv4.GetAddressBytes();
        if (want.Length != 4)
            return false;

        int size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return false;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0)
                return false;

            var numEntries = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpnetRow>();
            var rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MibIpnetRow>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.dwType is < 2 or > 4)
                    continue;
                if (row.dwPhysAddrLen != 6)
                    continue;

                var v = (uint)row.dwAddr;
                var got = new[]
                {
                    (byte)(v & 0xFF),
                    (byte)((v >> 8) & 0xFF),
                    (byte)((v >> 16) & 0xFF),
                    (byte)((v >> 24) & 0xFF)
                };
                if (!got.AsSpan().SequenceEqual(want))
                    continue;

                var phys = row.bPhysAddr ?? Array.Empty<byte>();
                macColonHex = string.Join(':',
                    phys.Take(6).Select(b => b.ToString("X2", global::System.Globalization.CultureInfo.InvariantCulture)));
                return !string.IsNullOrEmpty(macColonHex);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    /// <summary>SendARP DestIP must use the same ULONG layout as <c>inet_addr</c> / BitConverter on the address octets (see Windows samples).</summary>
    private static bool TrySendArpForIpv4(IPAddress ipv4, out string macColonHex)
    {
        macColonHex = string.Empty;
        var bytes = ipv4.GetAddressBytes();
        if (bytes.Length != 4)
            return false;
        var destIp = BitConverter.ToUInt32(bytes, 0);

        var mac = new byte[6];
        var len = mac.Length;
        var rc = SendARP(destIp, 0, mac, ref len);
        if (rc != 0 || len != 6)
            return false;

        macColonHex = string.Join(':',
            mac.Take(6).Select(b => b.ToString("X2", global::System.Globalization.CultureInfo.InvariantCulture)));
        return true;
    }

    /// <summary>Maps normalized MAC (AA:BB:...) to IPv4 from system ARP cache.</summary>
    internal static bool TryFindIpv4ForMac(string normalizedMacColon, out IPAddress? address)
    {
        address = null;
        var map = ReadArpMacToIp();
        return map.TryGetValue(normalizedMacColon, out address);
    }

    internal static IReadOnlyDictionary<string, IPAddress> ReadArpMacToIp()
    {
        var map = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        int size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return map;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var ret = GetIpNetTable(buffer, ref size, false);
            if (ret != 0)
                return map;

            var numEntries = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpnetRow>();
            var rowPtr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MibIpnetRow>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (row.dwType is < 2 or > 4)
                    continue;

                if (row.dwPhysAddrLen != 6)
                    continue;

                var phys = row.bPhysAddr ?? Array.Empty<byte>();
                var mac = string.Join(':',
                    phys.Take(6).Select(b => b.ToString("X2", global::System.Globalization.CultureInfo.InvariantCulture)));

                // MIB_IPNETROW.dwAddr: IPv4 octets map to the DWORD so (dwAddr & 0xFF) is the first dotted quad (MSDN samples).
                var v = (uint)row.dwAddr;
                var ip = new IPAddress(new[]
                {
                    (byte)(v & 0xFF),
                    (byte)((v >> 8) & 0xFF),
                    (byte)((v >> 16) & 0xFF),
                    (byte)((v >> 24) & 0xFF)
                });
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any))
                    continue;
                if (!map.ContainsKey(mac))
                    map[mac] = ip;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpnetRow
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] bPhysAddr;
        public int dwAddr;
        public int dwType;
    }
}
