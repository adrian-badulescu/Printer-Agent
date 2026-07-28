using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.Persistence;

/// <summary>
/// Ține câmpuri din <c>agent.json</c> (ProgramData) aliniate cu install-dir / sesiunea (vizibilitate operatori).
/// </summary>
public static class AgentProgramDataAgentJsonSync
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Copiază <c>Version</c> din install-dir în ProgramData când MSI / auto-update bump-uiește binarele.</summary>
    public static void TryWriteVersionFromInstallDir(string installDirAgentJson, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(installDirAgentJson) || !File.Exists(installDirAgentJson))
            return;

        string? installVersion;
        try
        {
            installVersion = JsonDocument.Parse(File.ReadAllText(installDirAgentJson))
                .RootElement.GetProperty("Version").GetString()?.Trim();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(installVersion))
            return;

        TryWriteVersion(installVersion, programDataAgentJsonPath: null, logger);
    }

    public static void TryWriteVersion(string version, string? programDataAgentJsonPath = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        var path = programDataAgentJsonPath ?? Path.Combine(AgentProgramData.Root, "agent.json");
        try
        {
            if (!File.Exists(path))
                return;

            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text, documentOptions: AgentJsonDocumentOptions.ForRead);
            if (node is not JsonObject root)
                return;

            var current = root["Version"]?.GetValue<string>();
            if (string.Equals(current, version, StringComparison.Ordinal))
                return;

            root["Version"] = version;
            AgentProgramDataJsonWriter.WriteAtomic(path, root.ToJsonString(WriteOptions));
            logger?.LogInformation(
                "Synced Version in ProgramData agent.json: {OldVersion} -> {NewVersion}.",
                current ?? "(missing)",
                version);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not sync Version in ProgramData agent.json.");
        }
    }

    public static void TryWriteRestaurantId(string restaurantId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(restaurantId))
            return;

        var path = Path.Combine(AgentProgramData.Root, "agent.json");
        try
        {
            if (!File.Exists(path))
                return;

            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text, documentOptions: AgentJsonDocumentOptions.ForRead);
            if (node is not JsonObject root)
                return;

            var current = root["RestaurantId"]?.GetValue<string>();
            if (string.Equals(current, restaurantId, StringComparison.Ordinal))
                return;

            root["RestaurantId"] = restaurantId;
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            logger.LogInformation("Wrote RestaurantId to agent.json (session / enrollment).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update RestaurantId in agent.json.");
        }
    }
}
