using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Interfaces;
using PrinterAgent.Application.Storage;
using JsonOptions = PrinterAgent.Application.Storage.AgentJsonDocumentOptions;

namespace PrinterAgent.Infrastructure.Persistence;

public sealed class AgentPrinterConfigurationUpdater : IAgentPrinterConfigurationUpdater
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly ILogger<AgentPrinterConfigurationUpdater> _logger;

    public AgentPrinterConfigurationUpdater(ILogger<AgentPrinterConfigurationUpdater> logger)
    {
        _logger = logger;
    }

    private static string AgentJsonPath => Path.Combine(AgentProgramData.Root, "agent.json");

    public bool TryPatchPrinter(
        string printerId,
        string? ipAddress = null,
        string? macAddress = null,
        bool? fallbackProvisional = null,
        string? lastDiscoveryNote = null)
    {
        if (string.IsNullOrWhiteSpace(printerId))
            return false;

        try
        {
            if (!File.Exists(AgentJsonPath))
                return false;

            var text = File.ReadAllText(AgentJsonPath);
            var node = JsonNode.Parse(text, documentOptions: JsonOptions.ForRead);
            if (node is not JsonObject root)
                return false;

            var printers = root["Printers"] as JsonArray ?? new JsonArray();
            root["Printers"] = printers;

            JsonObject? target = null;
            foreach (var item in printers)
            {
                if (item is not JsonObject o)
                    continue;
                var entryId = o["id"]?.GetValue<string>() ?? o["Id"]?.GetValue<string>();
                if (string.Equals(entryId, printerId, StringComparison.OrdinalIgnoreCase))
                {
                    target = o;
                    break;
                }
            }

            if (target == null)
                return false;

            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                target["ipAddress"] = ipAddress.Trim();
                target.Remove("IpAddress");
            }

            if (!string.IsNullOrWhiteSpace(macAddress))
            {
                target["macAddress"] = macAddress.Trim();
                target.Remove("MacAddress");
            }

            if (fallbackProvisional.HasValue)
            {
                target["fallbackProvisional"] = fallbackProvisional.Value;
                target.Remove("FallbackProvisional");
            }

            if (lastDiscoveryNote != null)
            {
                target["lastDiscoveryNote"] = lastDiscoveryNote;
                target.Remove("LastDiscoveryNote");
            }

            Directory.CreateDirectory(AgentProgramData.Root);
            File.WriteAllText(AgentJsonPath, root.ToJsonString(WriteOptions));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not patch printer {PrinterId} in agent.json.", printerId);
            return false;
        }
    }
}
