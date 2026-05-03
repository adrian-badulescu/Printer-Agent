using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IBackendClient
{
    Task UpdateJobStatusAsync(string jobId, PrintJobStatus status, CancellationToken cancellationToken = default);
    /// <summary>Returns false when API responds 401 Unauthorized (token invalid/expired).</summary>
    Task<bool> SendHeartbeatAsync(AgentInfo agentInfo, CancellationToken cancellationToken = default);
    Task<AgentUpdateResponse?> CheckForUpdatesAsync(string agentId, CancellationToken cancellationToken = default);

    Task<AgentEnrollResponse?> EnrollAsync(string enrollmentCode, Guid clientInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Downloads the WireGuard client config (.conf) for this agent, or null when not available.</summary>
    Task<string?> GetWireGuardConfAsync(string agentId, CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(Uri url, CancellationToken cancellationToken = default);
}

public sealed class AgentEnrollResponse
{
    public string AgentId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string RestaurantId { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}
