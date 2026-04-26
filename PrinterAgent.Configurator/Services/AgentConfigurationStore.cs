using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Configurator.Services;

/// <summary>
/// Citește și actualizează <c>agent.json</c> sub <see cref="AgentProgramData.Root"/> păstrând chei necunoscute.
/// </summary>
public sealed class AgentConfigurationStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string AgentJsonPath => Path.Combine(AgentProgramData.Root, "agent.json");

    public JsonObject LoadOrCreateTemplate()
    {
        if (File.Exists(AgentJsonPath))
        {
            var text = File.ReadAllText(AgentJsonPath);
            var node = JsonNode.Parse(text, documentOptions: AgentJsonDocumentOptions.ForRead);
            if (node is JsonObject o)
                return o;
        }

        return CreateDefaultTemplate();
    }

    public void Save(JsonObject root)
    {
        Directory.CreateDirectory(AgentProgramData.Root);
        File.WriteAllText(AgentJsonPath, root.ToJsonString(WriteOptions));
    }

    private static JsonObject CreateDefaultTemplate()
    {
        var o = new JsonObject
        {
            ["RestaurantId"] = "",
            ["EnrollmentCode"] = "",
            ["BackendUrl"] = "http://localhost:7051",
            ["BackendJwtToken"] = "",
            ["UpdateSignatureSecret"] = "change-me-same-as-backend-PrinterAgent",
            ["Version"] = "1.0.7",
            ["MaxPrintRetryAttempts"] = 5,
            ["PrintRetryBaseDelayMs"] = 1000,
            ["PrinterConnectTimeoutSeconds"] = 15,
            ["Connectivity"] = new JsonObject
            {
                ["VerifyAtStartup"] = true,
                ["BackendHealthPath"] = "api/ping-lite",
                ["BackendHealthTimeoutSeconds"] = 10
            },
            ["WireGuard"] = new JsonObject
            {
                ["Enabled"] = false,
                ["ConfigFilePath"] = "C:\\\\ProgramData\\\\URSPrinterAgent\\\\restaurant-tunnel.conf",
                ["WindowsTunnelServiceName"] = "WireGuardTunnel$restaurant-tunnel",
                ["WaitForTunnelServiceSeconds"] = 120,
                ["StartServiceIfStopped"] = false
            },
            ["Redis"] = new JsonObject
            {
                ["Host"] = "127.0.0.1",
                ["Port"] = "6379",
                ["User"] = "printer-agent",
                ["Password"] = "redis-acl-password",
                ["Ssl"] = false,
                ["AbortConnect"] = false,
                ["ClientName"] = "URSPrinterAgent",
                ["StreamKeyPrefix"] = "print.jobs",
                ["ConsumerGroup"] = "printer-agents"
            },
            ["Printers"] = new JsonArray()
        };
        return o;
    }

    public static string ToPrinterIdSlug(string displayName, IEnumerable<string> existingIds)
    {
        static string RemoveDiacritics(string s)
        {
            var norm = s.Normalize(System.Text.NormalizationForm.FormD);
            var chars = norm.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray();
            return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
        }

        var baseSlug = RemoveDiacritics(displayName.Trim().ToLowerInvariant());
        var chars = baseSlug.Select(c => char.IsLetterOrDigit(c) ? c : (c is ' ' or '-' ? '-' : '_')).ToArray();
        var collapsed = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var ch in new[] { '_', '.', '/' })
            collapsed = collapsed.Replace(ch.ToString(), "-", StringComparison.Ordinal);
        while (collapsed.Contains("--", StringComparison.Ordinal))
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        collapsed = collapsed.Trim('-');
        if (string.IsNullOrEmpty(collapsed))
            collapsed = "printer";

        var id = collapsed;
        var n = 2;
        var taken = new HashSet<string>(existingIds.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
        while (taken.Contains(id))
        {
            id = $"{collapsed}-{n}";
            n++;
        }

        return id;
    }
}
