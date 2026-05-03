using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public sealed class PrinterRecoveryResult
{
    public bool Recovered { get; init; }
    public Printer? Printer { get; init; }
    public string? TelemetryNote { get; init; }
}

/// <summary>Resolves printer IP via MAC/ARP and optional parallel subnet scan on port.</summary>
public interface IPrinterDiscoveryService
{
    /// <summary>After TCP failures to the configured printer — MAC lookup, then subnet scan with cooldown.</summary>
    Task<PrinterRecoveryResult> TryRecoverAfterPrintFailureAsync(Printer printer, CancellationToken cancellationToken = default);

    /// <summary>Lightweight ARP-only refresh for heartbeat reporting (no subnet scan).</summary>
    Task<IReadOnlyList<Printer>> MergeArpEndpointsAsync(IReadOnlyList<Printer> printers, CancellationToken cancellationToken = default);
}
