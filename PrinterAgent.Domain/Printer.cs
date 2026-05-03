namespace PrinterAgent.Domain;

public class Printer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public PrinterStatus Status { get; set; } = PrinterStatus.Offline;

    /// <summary>Optional stable LAN identity (e.g. Ethernet NIC). Used for ARP-based IP refresh.</summary>
    public string? MacAddress { get; set; }

    /// <summary>True when IP was set from degraded fallback (first host with port open on subnet).</summary>
    public bool FallbackProvisional { get; set; }

    /// <summary>Optional short note for heartbeat/JSON (e.g. discovery reason).</summary>
    public string? LastDiscoveryNote { get; set; }
}
