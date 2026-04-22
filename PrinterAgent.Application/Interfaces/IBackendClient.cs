using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IBackendClient
{
    Task UpdateJobStatusAsync(string jobId, PrintJobStatus status, CancellationToken cancellationToken = default);
    /// <summary>Returns false when API responds 401 Unauthorized (token invalid/expired).</summary>
    Task<bool> SendHeartbeatAsync(AgentInfo agentInfo, CancellationToken cancellationToken = default);
    Task<AgentUpdateResponse?> CheckForUpdatesAsync(string agentId, CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(Uri url, CancellationToken cancellationToken = default);
}
