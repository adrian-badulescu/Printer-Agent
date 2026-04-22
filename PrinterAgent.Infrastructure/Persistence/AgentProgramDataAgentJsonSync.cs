using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Infrastructure.Persistence;

/// <summary>
/// Ține <c>RestaurantId</c> din <c>agent.json</c> aliniat cu sesiunea după enroll / refresh (vizibilitate pentru operatori).
/// </summary>
internal static class AgentProgramDataAgentJsonSync
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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
            logger.LogInformation("RestaurantId scris în agent.json (sesiune / enroll).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nu s-a putut actualiza RestaurantId în agent.json.");
        }
    }
}
