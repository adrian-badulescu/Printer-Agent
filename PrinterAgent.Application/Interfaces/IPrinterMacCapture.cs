namespace PrinterAgent.Application.Interfaces;

/// <summary>Captures NIC MAC from ARP after a successful TCP session to an IPv4 host.</summary>
public interface IPrinterMacCapture
{
    /// <summary>If MAC is missing in config, resolve via ARP for <paramref name="remoteIpv4"/> and persist.</summary>
    Task TryPersistMacAfterSuccessfulPrintAsync(string printerId, string remoteIpv4, CancellationToken cancellationToken = default);
}
