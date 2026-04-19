namespace PrinterAgent.Domain;

public class AgentInfo
{
    public string AgentId { get; set; } = string.Empty;
    public string RestaurantId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<Printer> Printers { get; set; } = new();
}

public class AgentUpdateResponse
{
    public bool UpdateAvailable { get; set; }
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}
