using System.IO;
using System.Text.Json;
using PrinterAgent.Application.Storage;

namespace PrinterAgent.Configurator.Services;

/// <summary>
/// Reads enrollment session metadata from <c>agent.session.json</c> without decrypting DPAPI tokens.
/// Used by the Configurator to skip the enrollment step when the agent PC is already enrolled.
/// </summary>
public sealed class AgentSessionProbe
{
    private const string SessionFileName = "agent.session.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string SessionFilePath => Path.Combine(AgentProgramData.Root, SessionFileName);

    public bool HasUsableSession(TimeSpan expirySkew)
    {
        if (!File.Exists(SessionFilePath))
            return false;

        try
        {
            var json = File.ReadAllText(SessionFilePath);
            var dto = JsonSerializer.Deserialize<AgentSessionMetadataDto>(json, SerializerOptions);
            if (dto == null)
                return false;

            if (string.IsNullOrWhiteSpace(dto.AgentId) || string.IsNullOrWhiteSpace(dto.RestaurantId))
                return false;

            var limit = DateTime.UtcNow.Subtract(expirySkew);
            return dto.ExpiresAtUtc > limit;
        }
        catch
        {
            return false;
        }
    }

    private sealed class AgentSessionMetadataDto
    {
        public string AgentId { get; set; } = string.Empty;
        public string? RestaurantId { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }
}
