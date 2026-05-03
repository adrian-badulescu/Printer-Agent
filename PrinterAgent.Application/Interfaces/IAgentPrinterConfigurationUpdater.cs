namespace PrinterAgent.Application.Interfaces;

/// <summary>Updates printer entries in %ProgramData%\URSPrinterAgent\agent.json while preserving other keys.</summary>
public interface IAgentPrinterConfigurationUpdater
{
    bool TryPatchPrinter(
        string printerId,
        string? ipAddress = null,
        string? macAddress = null,
        bool? fallbackProvisional = null,
        string? lastDiscoveryNote = null);
}
