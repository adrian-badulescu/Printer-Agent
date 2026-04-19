using PrinterAgent.Domain;

namespace PrinterAgent.Application.Interfaces;

public interface IBackendClient
{
    Task UpdateJobStatusAsync(string jobId, PrintJobStatus status, CancellationToken cancellationToken = default);
    Task SendHeartbeatAsync(AgentInfo agentInfo, CancellationToken cancellationToken = default);
    Task<AgentUpdateResponse?> CheckForUpdatesAsync(string agentId, CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(Uri url, CancellationToken cancellationToken = default);
}
