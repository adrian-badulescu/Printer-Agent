using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Storage;
using PrinterAgent.Domain;
using PrinterAgent.Infrastructure.Networking;
using PrinterAgent.Infrastructure.Persistence;

namespace PrinterAgent.Configurator.Services;

/// <summary>Runs the same IP recovery as the Windows service (ARP, then optional subnet scan) and updates ProgramData agent.json.</summary>
public static class PrinterIpRefreshService
{
    private static readonly JsonSerializerOptions PrinterJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<(bool ok, string message)> TryRefreshAllPrintersAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AgentProgramData.Root, "agent.json");
        if (!File.Exists(path))
            return (false, "agent.json not found in ProgramData.");

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("Printers", out var printersEl) || printersEl.ValueKind != JsonValueKind.Array)
            return (false, "No Printers array in agent.json.");

        var printers = JsonSerializer.Deserialize<List<Printer>>(printersEl.GetRawText(), PrinterJsonOptions) ?? new List<Printer>();
        if (printers.Count == 0)
            return (false, "No printers configured.");

        var logFactory = LoggerFactory.Create(b => { });
        var updater = new AgentPrinterConfigurationUpdater(logFactory.CreateLogger<AgentPrinterConfigurationUpdater>());
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        var discovery = new PrinterDiscoveryService(
            updater,
            logFactory.CreateLogger<PrinterDiscoveryService>(),
            configuration);

        var lines = new List<string>();
        foreach (var p in printers)
        {
            var r = await discovery.TryRecoverAfterPrintFailureAsync(p, cancellationToken).ConfigureAwait(false);
            var line = r.Recovered && r.Printer != null
                ? $"{p.Id}: {r.Printer.IpAddress} ({r.TelemetryNote})"
                : $"{p.Id}: no change ({r.TelemetryNote})";
            lines.Add(line);
        }

        return (true, string.Join(Environment.NewLine, lines));
    }
}
