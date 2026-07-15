namespace PrinterAgent.Domain;

public class AgentInfo
{
    public string AgentId { get; set; } = string.Empty;
    public string RestaurantId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<Printer> Printers { get; set; } = new();

    /// <summary>HTTP base URL for LAN offline print API (e.g. http://192.168.1.50:9247).</summary>
    public string? LocalApiBaseUrl { get; set; }

    /// <summary>Bearer token for POST /local/print-jobs from staff primary offline device.</summary>
    public string? LocalPrintApiToken { get; set; }
}

public class AgentUpdateResponse
{
    public bool UpdateAvailable { get; set; }
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}
